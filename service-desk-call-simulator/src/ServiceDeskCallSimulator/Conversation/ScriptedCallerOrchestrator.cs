using System.Threading.Channels;
using System.Runtime.ExceptionServices;
using ServiceDeskCallSimulator.Calls;
using ServiceDeskCallSimulator.Media;
using ServiceDeskCallSimulator.Monitoring;
using ServiceDeskCallSimulator.Presets;
using ServiceDeskCallSimulator.Speech;

namespace ServiceDeskCallSimulator.Conversation;

/// <summary>
/// Time and buffer limits applied to one scripted caller conversation.
/// </summary>
public sealed record ScriptedCallerOrchestratorOptions
{
    /// <summary>
    /// Gets the deadline for ACS call connection.
    /// </summary>
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the deadline for ACS media readiness.
    /// </summary>
    public TimeSpan MediaReadyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the deadline for recognizer initialization.
    /// </summary>
    public TimeSpan RecognitionStartTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets the deadline for native recognizer shutdown during terminal cleanup.
    /// </summary>
    public TimeSpan RecognitionStopTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the deadline for one synthesis request.
    /// </summary>
    public TimeSpan SynthesisTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Gets the deadline to drain one caller audio generation.
    /// </summary>
    public TimeSpan GenerationDrainTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the deadline for media send and stop operations.
    /// </summary>
    public TimeSpan MediaOperationTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the maximum synthesis result retained for sequential frame delivery.
    /// </summary>
    public int MaximumSynthesisBytes { get; init; } = 1_024 * 1_024;

    /// <summary>
    /// Gets the bounded count of pending speech updates.
    /// </summary>
    public int RecognitionUpdateCapacity { get; init; } = 100;
}

/// <summary>
/// Runs one script-grounded caller conversation against an already-started ACS call session.
/// </summary>
public sealed class ScriptedCallerOrchestrator : IAsyncDisposable
{
    private static readonly TimeSpan PcmFrameDuration = TimeSpan.FromMilliseconds(20);
    private readonly CallerScriptSnapshot _script;
    private readonly ICallerCallSession _callSession;
    private readonly ICallMediaTransport _mediaTransport;
    private readonly IGroundedReplyGenerator _replyGenerator;
    private readonly ISpeechPipeline _speech;
    private readonly IAudioMonitor _audioMonitor;
    private readonly AudioMonitorSourceGate _monitorSource = new();
    private readonly ScriptedCallerOrchestratorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<SpeechRecognitionUpdate> _recognitionUpdates;
    private readonly SemaphoreSlim _modelTurnGate = new(1, 1);
    private readonly object _sync = new();
    private readonly object _startSync = new();
    private readonly object _disposeSync = new();
    private readonly object _resourceStopSync = new();
    private readonly object _terminalCleanupSync = new();
    private readonly List<TranscriptTurn> _transcript = [];
    private readonly HashSet<string> _processedFinalSegmentIds = new(StringComparer.Ordinal);
    private Task? _startTask;
    private Task? _inboundTask;
    private Task? _recognitionTask;
    private Task? _disconnectTask;
    private Task? _disposeTask;
    private Task? _stopResourcesTask;
    private Task? _terminalCleanupTask;
    private Playback? _activePlayback;
    private Exception? _terminalCleanupFailure;
    private CallerActivityState _activity = CallerActivityState.Idle;
    private bool _openingLineSent;
    private int _faulted;

    /// <summary>
    /// Initializes a scripted caller for a concrete, already-started ACS session.
    /// </summary>
    public ScriptedCallerOrchestrator(
        CallerScriptSnapshot script,
        AcsCallSession callSession,
        IGroundedReplyGenerator replyGenerator,
        ISpeechPipeline speech,
        IAudioMonitor audioMonitor,
        ScriptedCallerOrchestratorOptions? options = null,
        TimeProvider? timeProvider = null)
        : this(
            script,
            (ICallerCallSession)(callSession ?? throw new ArgumentNullException(nameof(callSession))),
            replyGenerator,
            speech,
            audioMonitor,
            options,
            timeProvider)
    {
    }

    internal ScriptedCallerOrchestrator(
        CallerScriptSnapshot script,
        ICallerCallSession callSession,
        IGroundedReplyGenerator replyGenerator,
        ISpeechPipeline speech,
        IAudioMonitor audioMonitor,
        ScriptedCallerOrchestratorOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _script = script ?? throw new ArgumentNullException(nameof(script));
        _callSession = callSession ?? throw new ArgumentNullException(nameof(callSession));
        _mediaTransport = callSession.CallerMediaTransport
            ?? throw new ArgumentException("The call session must provide media transport.", nameof(callSession));
        _replyGenerator = replyGenerator ?? throw new ArgumentNullException(nameof(replyGenerator));
        _speech = speech ?? throw new ArgumentNullException(nameof(speech));
        _audioMonitor = audioMonitor ?? throw new ArgumentNullException(nameof(audioMonitor));
        _options = options ?? new ScriptedCallerOrchestratorOptions();
        ValidateOptions(_options);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _recognitionUpdates = Channel.CreateBounded<SpeechRecognitionUpdate>(
            new BoundedChannelOptions(_options.RecognitionUpdateCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
    }

    /// <summary>
    /// Gets the immutable script facts used by this call.
    /// </summary>
    public CallerScriptSnapshot Script => _script;

    /// <summary>
    /// Gets the latest caller activity state.
    /// </summary>
    public CallerActivityState ActivityState
    {
        get
        {
            lock (_sync)
            {
                return _activity;
            }
        }
    }

    /// <summary>
    /// Gets whether terminal resource cleanup encountered a failure.
    /// </summary>
    public bool HasTerminalCleanupFailure => Volatile.Read(ref _terminalCleanupFailure) is not null;

    /// <summary>
    /// Returns a stable snapshot of timestamped conversation turns for UI rendering.
    /// </summary>
    public IReadOnlyList<TranscriptTurn> Transcript
    {
        get
        {
            lock (_sync)
            {
                return _transcript.ToArray();
            }
        }
    }

    /// <summary>
    /// Gets the completed conversation turns supplied to the grounded caller model. Interim
    /// recognition fragments stay in <see cref="Transcript"/> for the UI but never reach the model.
    /// </summary>
    private IReadOnlyList<TranscriptTurn> FinalTranscript
    {
        get
        {
            lock (_sync)
            {
                return _transcript
                    .Where(turn => turn.Status == TranscriptStatus.Final)
                    .ToArray();
            }
        }
    }

    /// <summary>
    /// Raised on a worker context when a transcript item is appended.
    /// UI clients should marshal this event to their UI context.
    /// </summary>
    public event EventHandler<TranscriptTurn>? TranscriptUpdated;

    /// <summary>
    /// Raised on a worker context when the caller activity changes.
    /// UI clients should marshal this event to their UI context.
    /// </summary>
    public event EventHandler<CallerActivityChange>? ActivityChanged;

    /// <summary>
    /// Starts recognition and media consumption while waiting for the remote greeting to finish.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_startSync)
        {
            if (_startTask is not null)
            {
                throw new InvalidOperationException("A scripted caller can be started only once.");
            }

            _startTask = StartCoreAsync(cancellationToken);
            return _startTask;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            await _callSession.ConnectionReady.WaitAsync(
                _options.ConnectionTimeout,
                _timeProvider,
                startupCancellation.Token).ConfigureAwait(false);
            await _mediaTransport.ConnectionReady.WaitAsync(
                _options.MediaReadyTimeout,
                _timeProvider,
                startupCancellation.Token).ConfigureAwait(false);

            _speech.RecognitionUpdated += OnRecognitionUpdated;
            _callSession.StateChanged += OnCallStateChanged;
            await _speech.StartRecognitionAsync(_script.Locale, startupCancellation.Token)
                .WaitAsync(
                    _options.RecognitionStartTimeout,
                    _timeProvider,
                    startupCancellation.Token)
                .ConfigureAwait(false);

            _inboundTask = RunGuardedAsync(ProcessInboundFramesAsync);
            _recognitionTask = RunGuardedAsync(ProcessRecognitionUpdatesAsync);
            _disconnectTask = RunGuardedAsync(WatchForMediaDisconnectAsync);

            if (!_lifetime.IsCancellationRequested)
            {
                TransitionActivity(
                    CallerActivityState.Listening,
                    "Waiting for the service desk greeting to finish.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested
            || cancellationToken.IsCancellationRequested)
        {
            TransitionToTerminalStateForCall();
        }
        catch (PlaybackInterruptedException)
        {
            TransitionActivity(CallerActivityState.Listening, "Caller playback interrupted by barge-in.");
        }
        catch (Exception)
        {
            await FaultAsync("The caller conversation could not start.", hangUpCall: true).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ProcessInboundFramesAsync()
    {
        await foreach (var frame in _mediaTransport.InboundFrames.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
        {
            if (_monitorSource.ShouldMonitorInbound(frame.IsSilent))
            {
                _audioMonitor.TryMonitor(frame.Pcm16KMono);
            }

            if (!frame.IsSilent)
            {
                await _speech.WritePcmAsync(frame.Pcm16KMono, _lifetime.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessRecognitionUpdatesAsync()
    {
        await foreach (var update in _recognitionUpdates.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(update.Error))
            {
                AppendTranscript(TranscriptSpeaker.System, update.Error, TranscriptStatus.Final);
                await FaultAsync("Speech recognition failed.", hangUpCall: true).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(update.Text))
            {
                continue;
            }

            if (!update.IsFinal)
            {
                AppendTranscript(TranscriptSpeaker.ServiceDesk, update.Text, TranscriptStatus.Interim);
                continue;
            }

            var text = update.Text.Trim();
            if (!string.IsNullOrWhiteSpace(update.SegmentId))
            {
                lock (_sync)
                {
                    if (!_processedFinalSegmentIds.Add(update.SegmentId))
                    {
                        continue;
                    }
                }
            }

            AppendTranscript(TranscriptSpeaker.ServiceDesk, text, TranscriptStatus.Final);
            await ProcessFinalServiceDeskTurnAsync().ConfigureAwait(false);
        }
    }

    private async Task ProcessFinalServiceDeskTurnAsync()
    {
        await _modelTurnGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (_lifetime.IsCancellationRequested)
            {
                return;
            }

            if (!_openingLineSent)
            {
                _openingLineSent = true;
                AppendTranscript(TranscriptSpeaker.Caller, _script.OpeningLine, TranscriptStatus.Final);
                await SpeakAsync(_script.OpeningLine, _script.Voice, _lifetime.Token).ConfigureAwait(false);
                if (!_lifetime.IsCancellationRequested)
                {
                    TransitionActivity(CallerActivityState.Listening, "Opening line completed.");
                }

                return;
            }

            TransitionActivity(CallerActivityState.Thinking, "Waiting for a grounded caller decision.");
            var history = FinalTranscript;

            // One resolver decides both the grounded prompt language and the synthesis voice for
            // this turn, so the spoken language can never drift from the prompted language.
            var responseLanguage = CallerResponseLanguageResolver.Resolve(_script, history);
            var decision = await _replyGenerator.GenerateAsync(
                _script,
                history,
                _lifetime.Token).ConfigureAwait(false);
            if (decision.Action == GroundedReplyAction.HangUp)
            {
                await EndAfterDecisionAsync(decision, responseLanguage.Voice).ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrWhiteSpace(decision.SpokenText))
            {
                AppendTranscript(TranscriptSpeaker.Caller, decision.SpokenText, TranscriptStatus.Final);
                await SpeakAsync(decision.SpokenText, responseLanguage.Voice, _lifetime.Token).ConfigureAwait(false);
            }

            if (!_lifetime.IsCancellationRequested)
            {
                TransitionActivity(CallerActivityState.Listening, "Caller reply completed.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // A caller, remote party, or call lifecycle ended the active turn.
        }
        catch (PlaybackInterruptedException)
        {
            TransitionActivity(CallerActivityState.Listening, "Caller playback interrupted by barge-in.");
        }
        catch (GroundedReplyException)
        {
            await FaultAsync("The grounded caller decision was invalid.", hangUpCall: true).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await FaultAsync("The caller conversation turn failed.", hangUpCall: true).ConfigureAwait(false);
        }
        finally
        {
            _modelTurnGate.Release();
        }
    }

    private async Task EndAfterDecisionAsync(GroundedModelDecision decision, string voice)
    {
        TransitionActivity(CallerActivityState.Ending, "The caller model completed the interaction.");
        if (!string.IsNullOrWhiteSpace(decision.SpokenText))
        {
            AppendTranscript(TranscriptSpeaker.Caller, decision.SpokenText, TranscriptStatus.Final);
            using var drainDeadline = new CancellationTokenSource(
                _options.GenerationDrainTimeout,
                _timeProvider);
            using var drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token,
                drainDeadline.Token);
            await SpeakAsync(decision.SpokenText, voice, drainCancellation.Token).ConfigureAwait(false);
        }

        await _callSession.HangUpAsync(_lifetime.Token).ConfigureAwait(false);
        TransitionActivity(CallerActivityState.Ended, "Caller requested hang-up.");
    }

    /// <summary>
    /// Resolves the caller response language for the current transcript state. Recognition of the
    /// remote service desk is unaffected: it always stays on the script's own locale.
    /// </summary>
    private CallerResponseLanguage ResolveResponseLanguage() =>
        CallerResponseLanguageResolver.Resolve(_script, FinalTranscript);

    private async Task SpeakAsync(string text, string voice, CancellationToken cancellationToken)
    {
        TransitionActivity(CallerActivityState.Speaking, "Caller audio playback started.");
        var pcm = await _speech.SynthesizeAsync(voice, text, cancellationToken)
            .WaitAsync(_options.SynthesisTimeout, _timeProvider, cancellationToken)
            .ConfigureAwait(false);
        if (pcm.Length == 0 || pcm.Length % sizeof(short) != 0 || pcm.Length > _options.MaximumSynthesisBytes)
        {
            throw new InvalidOperationException("Speech synthesis returned invalid or oversized raw PCM.");
        }

        await SendPcmGenerationAsync(pcm, cancellationToken)
            .WaitAsync(_options.GenerationDrainTimeout, _timeProvider, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SendPcmGenerationAsync(byte[] pcm, CancellationToken cancellationToken)
    {
        using var playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var playback = new Playback(
            _mediaTransport.CreateAudioGeneration(),
            playbackCancellation);
        lock (_sync)
        {
            if (_lifetime.IsCancellationRequested)
            {
                throw new OperationCanceledException(_lifetime.Token);
            }

            _activePlayback = playback;
        }

        // Caller playback owns the local monitor for this generation so inbound remote frames
        // cannot interleave with outbound frames in the single monitor FIFO.
        _monitorSource.BeginOutbound();
        try
        {
            var playbackStartedAt = _timeProvider.GetTimestamp();
            var frameIndex = 0;
            for (var offset = 0; offset < pcm.Length; offset += AcsMediaTransport.PcmFrameBytes)
            {
                playbackCancellation.Token.ThrowIfCancellationRequested();
                if (frameIndex > 0)
                {
                    var targetElapsed = TimeSpan.FromTicks(PcmFrameDuration.Ticks * frameIndex);
                    var remaining = targetElapsed - _timeProvider.GetElapsedTime(playbackStartedAt);
                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(remaining, _timeProvider, playbackCancellation.Token)
                            .ConfigureAwait(false);
                    }
                }

                var frame = new byte[AcsMediaTransport.PcmFrameBytes];
                var count = Math.Min(frame.Length, pcm.Length - offset);
                Buffer.BlockCopy(pcm, offset, frame, 0, count);
                _audioMonitor.TryMonitorOutbound(frame);

                await _mediaTransport.SendAudioAsync(playback.Generation, frame, playbackCancellation.Token)
                    .WaitAsync(
                        _options.MediaOperationTimeout,
                        _timeProvider,
                        playbackCancellation.Token)
                    .ConfigureAwait(false);
                frameIndex++;
            }
        }
        catch (OperationCanceledException) when (playback.Cancellation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested
            && !_lifetime.IsCancellationRequested)
        {
            throw new PlaybackInterruptedException();
        }
        finally
        {
            _monitorSource.EndOutbound();
            lock (_sync)
            {
                if (ReferenceEquals(_activePlayback, playback))
                {
                    _activePlayback = null;
                }
            }

            playback.Cancellation.Dispose();
        }
    }

    private void OnRecognitionUpdated(object? sender, SpeechRecognitionUpdate update)
    {
        if (!update.IsFinal && !string.IsNullOrWhiteSpace(update.Text))
        {
            _ = StopActivePlaybackForBargeInAsync();
        }

        _recognitionUpdates.Writer.TryWrite(update);
    }

    private void OnCallStateChanged(object? sender, CallStateChange change)
    {
        if (change.CurrentState is CallSessionState.Ending or CallSessionState.Ended or CallSessionState.Faulted)
        {
            StopForCallTermination();
        }
    }

    private async Task WatchForMediaDisconnectAsync()
    {
        await _mediaTransport.Disconnected.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        StopForCallTermination();
    }

    private async Task StopActivePlaybackForBargeInAsync()
    {
        Playback? playback;
        lock (_sync)
        {
            playback = _activePlayback;
        }

        if (playback is null)
        {
            return;
        }

        try
        {
            playback.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (Interlocked.Exchange(ref playback.StopRequested, 1) == 0)
        {
            try
            {
                await _mediaTransport.StopAudioAsync(playback.Generation)
                    .WaitAsync(
                        _options.MediaOperationTimeout,
                        _timeProvider,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // Call shutdown owns the media socket.
            }
            catch (Exception)
            {
                if (!_lifetime.IsCancellationRequested)
                {
                    await FaultAsync("Caller playback could not be stopped for barge-in.", hangUpCall: true)
                        .ConfigureAwait(false);
                }
            }
        }

        if (!_lifetime.IsCancellationRequested)
        {
            TransitionActivity(CallerActivityState.Listening, "Caller playback interrupted by barge-in.");
        }
    }

    private void StopForCallTermination()
    {
        _ = BeginTerminalCleanup();
    }

    private async Task FaultAsync(string safeReason, bool hangUpCall)
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0)
        {
            return;
        }

        AppendTranscript(TranscriptSpeaker.System, safeReason, TranscriptStatus.Final);
        TransitionActivity(CallerActivityState.Faulted, safeReason);
        _ = BeginTerminalCleanup();
        if (hangUpCall && _callSession.State is not (CallSessionState.Ended or CallSessionState.Faulted))
        {
            try
            {
                await _callSession.HangUpAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The call session exposes its own terminal ACS error state.
            }
        }
    }

    private Task BeginTerminalCleanup()
    {
        lock (_terminalCleanupSync)
        {
            return _terminalCleanupTask ??= Task.Run(TerminalCleanupCoreAsync);
        }
    }

    private async Task TerminalCleanupCoreAsync()
    {
        Exception? cleanupFailure = null;
        _lifetime.Cancel();
        _recognitionUpdates.Writer.TryComplete();
        try
        {
            await StopActivePlaybackForBargeInAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        try
        {
            await StopResourcesAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = CombineFailures(cleanupFailure, exception);
        }

        try
        {
            await AwaitBackgroundTasksAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = CombineFailures(cleanupFailure, exception);
        }

        try
        {
            await DisposeTerminalResourcesAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = CombineFailures(cleanupFailure, exception);
        }

        if (cleanupFailure is not null)
        {
            RecordTerminalCleanupFailure(cleanupFailure);
        }

        TransitionToTerminalStateForCall();

        if (cleanupFailure is not null)
        {
            throw new InvalidOperationException("The caller conversation cleanup failed.", cleanupFailure);
        }
    }

    private Task StopResourcesAsync()
    {
        lock (_resourceStopSync)
        {
            return _stopResourcesTask ??= StopResourcesCoreAsync();
        }
    }

    private async Task StopResourcesCoreAsync()
    {
        _speech.RecognitionUpdated -= OnRecognitionUpdated;
        _callSession.StateChanged -= OnCallStateChanged;
        Exception? cleanupFailure = null;
        try
        {
            await _speech.StopRecognitionAsync()
                .WaitAsync(_options.RecognitionStopTimeout, _timeProvider)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        try
        {
            await _audioMonitor.StopAsync()
                .WaitAsync(_options.MediaOperationTimeout, _timeProvider)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = CombineFailures(cleanupFailure, exception);
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    private async Task DisposeTerminalResourcesAsync()
    {
        Exception? cleanupFailure = null;
        try
        {
            await _speech.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        try
        {
            await _audioMonitor.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = CombineFailures(cleanupFailure, exception);
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    private async Task DisposeCoreAsync()
    {
        var terminalCleanup = BeginTerminalCleanup();
        if (_callSession.State is not (CallSessionState.Ended or CallSessionState.Faulted))
        {
            try
            {
                await _callSession.HangUpAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The call session owns ACS terminal error reporting.
            }
        }

        await terminalCleanup.ConfigureAwait(false);
    }

    private async Task AwaitBackgroundTasksAsync()
    {
        foreach (var task in new[] { _startTask, _inboundTask, _recognitionTask, _disconnectTask })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Lifetime cancellation is expected during cleanup.
            }
            catch (Exception)
            {
                // Fault paths already transition the conversation before terminal cleanup.
            }
        }
    }

    private async Task RunGuardedAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Call lifecycle cancellation is expected.
        }
        catch (Exception)
        {
            await FaultAsync("The caller media pipeline failed.", hangUpCall: true).ConfigureAwait(false);
        }
    }

    private void AppendTranscript(TranscriptSpeaker speaker, string text, TranscriptStatus status)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var turn = new TranscriptTurn(_timeProvider.GetUtcNow(), speaker, text.Trim(), status);
        lock (_sync)
        {
            _transcript.Add(turn);
        }

        TranscriptUpdated?.Invoke(this, turn);
    }

    private void TransitionActivity(CallerActivityState nextState, string reason)
    {
        CallerActivityChange? change = null;
        lock (_sync)
        {
            if (_activity == nextState || _activity == CallerActivityState.Faulted)
            {
                return;
            }

            change = new CallerActivityChange(_activity, nextState, _timeProvider.GetUtcNow(), reason);
            _activity = nextState;
        }

        ActivityChanged?.Invoke(this, change);
    }

    private void TransitionToTerminalStateForCall()
    {
        TransitionActivity(
            _callSession.State == CallSessionState.Faulted
                ? CallerActivityState.Faulted
                : CallerActivityState.Ended,
            "Caller conversation stopped.");
    }

    private void RecordTerminalCleanupFailure(Exception cleanupFailure)
    {
        if (Interlocked.CompareExchange(ref _terminalCleanupFailure, cleanupFailure, null) is not null)
        {
            return;
        }

        const string reason = "The caller conversation cleanup failed.";
        AppendTranscript(TranscriptSpeaker.System, reason, TranscriptStatus.Final);
        TransitionActivity(CallerActivityState.Faulted, reason);
    }

    private static Exception CombineFailures(Exception? existingFailure, Exception nextFailure) =>
        existingFailure is null
            ? nextFailure
            : new AggregateException(existingFailure, nextFailure);

    private static void ValidateOptions(ScriptedCallerOrchestratorOptions options)
    {
        if (options.ConnectionTimeout <= TimeSpan.Zero
            || options.MediaReadyTimeout <= TimeSpan.Zero
            || options.RecognitionStartTimeout <= TimeSpan.Zero
            || options.RecognitionStopTimeout <= TimeSpan.Zero
            || options.SynthesisTimeout <= TimeSpan.Zero
            || options.GenerationDrainTimeout <= TimeSpan.Zero
            || options.MediaOperationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Caller timeouts must be positive.");
        }

        if (options.MaximumSynthesisBytes <= 0 || options.RecognitionUpdateCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Caller buffer limits must be positive.");
        }
    }

    private sealed class Playback
    {
        public Playback(long generation, CancellationTokenSource cancellation)
        {
            Generation = generation;
            Cancellation = cancellation;
        }

        public long Generation { get; }

        public CancellationTokenSource Cancellation { get; }

        public int StopRequested;
    }

    private sealed class PlaybackInterruptedException : OperationCanceledException
    {
    }
}
