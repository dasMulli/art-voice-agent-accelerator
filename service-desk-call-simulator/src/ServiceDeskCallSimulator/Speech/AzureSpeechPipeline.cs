using Azure.Core;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace ServiceDeskCallSimulator.Speech;

/// <summary>
/// Azure Speech implementation that uses Entra credentials and native SDK resources per call.
/// </summary>
public sealed class AzureSpeechPipeline : ISpeechPipeline
{
    private static readonly TimeSpan SynthesisCancellationCleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RecognitionStopCleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly Uri _endpoint;
    private readonly TokenCredential _credential;
    private readonly object _sync = new();
    private AudioConfig? _recognitionAudioConfig;
    private PushAudioInputStream? _pushStream;
    private SpeechRecognizer? _recognizer;
    private Task? _recognitionStopTask;
    private int _disposed;

    /// <summary>
    /// Initializes an unstarted pipeline for the supplied shared credential.
    /// </summary>
    public AzureSpeechPipeline(Uri endpoint, TokenCredential credential)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
    }

    /// <inheritdoc />
    public event EventHandler<SpeechRecognitionUpdate>? RecognitionUpdated;

    /// <inheritdoc />
    public async Task StartRecognitionAsync(string locale, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        ThrowIfDisposed();

        SpeechRecognizer recognizer;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_recognizer is not null)
            {
                throw new InvalidOperationException("Speech recognition has already started for this call.");
            }

            if (_recognitionStopTask is not null)
            {
                throw new InvalidOperationException("Speech recognition cannot restart after it has stopped.");
            }

            var recognitionConfig = SpeechConfig.FromEndpoint(_endpoint, _credential);
            recognitionConfig.SpeechRecognitionLanguage = locale;
            _pushStream = AudioInputStream.CreatePushStream(
                AudioStreamFormat.GetWaveFormatPCM(samplesPerSecond: 16_000, bitsPerSample: 16, channels: 1));
            _recognitionAudioConfig = AudioConfig.FromStreamInput(_pushStream);
            recognizer = _recognizer = new SpeechRecognizer(recognitionConfig, _recognitionAudioConfig);
            recognizer.Recognizing += OnRecognizing;
            recognizer.Recognized += OnRecognized;
            recognizer.Canceled += OnCanceled;
        }

        try
        {
            await recognizer.StartContinuousRecognitionAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopRecognitionAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask WritePcmAsync(ReadOnlyMemory<byte> pcm16KMono, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (pcm16KMono.IsEmpty || pcm16KMono.Length % sizeof(short) != 0)
        {
            throw new ArgumentException("Inbound PCM must contain complete 16-bit mono samples.", nameof(pcm16KMono));
        }

        PushAudioInputStream pushStream;
        lock (_sync)
        {
            pushStream = _pushStream
                ?? throw new InvalidOperationException("Speech recognition has not started for this call.");
        }

        pushStream.Write(pcm16KMono.ToArray());
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<byte[]> SynthesizeAsync(
        string voice,
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(voice);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ThrowIfDisposed();

        var config = SpeechConfig.FromEndpoint(_endpoint, _credential);
        config.SpeechSynthesisVoiceName = voice;
        config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Raw16Khz16BitMonoPcm);
        using var synthesizer = new SpeechSynthesizer(config, audioConfig: null);
        var result = await SpeechSynthesisLifecycle.AwaitWithCancellationCleanupAsync(
                synthesizer.SpeakTextAsync(text),
                synthesizer.StopSpeakingAsync,
                cancellationToken,
                SynthesisCancellationCleanupTimeout,
                TimeProvider.System)
            .ConfigureAwait(false);
        if (result.Reason != ResultReason.SynthesizingAudioCompleted
            || result.AudioData is not { Length: > 0 } pcm
            || pcm.Length % sizeof(short) != 0)
        {
            throw new InvalidOperationException(
                "Azure Speech synthesis did not return raw 16 kHz, 16-bit, mono PCM audio.");
        }

        return pcm;
    }

    /// <inheritdoc />
    public Task StopRecognitionAsync()
    {
        SpeechRecognizer? recognizer;
        PushAudioInputStream? pushStream;
        AudioConfig? audioConfig;
        lock (_sync)
        {
            if (_recognitionStopTask is not null)
            {
                return _recognitionStopTask;
            }

            recognizer = _recognizer;
            pushStream = _pushStream;
            audioConfig = _recognitionAudioConfig;
            _recognizer = null;
            _pushStream = null;
            _recognitionAudioConfig = null;

            if (recognizer is null && pushStream is null && audioConfig is null)
            {
                return Task.CompletedTask;
            }

            _recognitionStopTask = StopRecognitionCoreAsync(recognizer, pushStream, audioConfig);
            return _recognitionStopTask;
        }
    }

    private async Task StopRecognitionCoreAsync(
        SpeechRecognizer? recognizer,
        PushAudioInputStream? pushStream,
        AudioConfig? audioConfig)
    {
        if (recognizer is not null)
        {
            recognizer.Recognizing -= OnRecognizing;
            recognizer.Recognized -= OnRecognized;
            recognizer.Canceled -= OnCanceled;
        }

        await SpeechRecognitionLifecycle.StopAndDisposeAsync(
                recognizer is null ? null : recognizer.StopContinuousRecognitionAsync,
                pushStream is null ? null : pushStream.Close,
                recognizer is null ? null : recognizer.Dispose,
                audioConfig is null ? null : audioConfig.Dispose,
                RecognitionStopCleanupTimeout,
                TimeProvider.System,
                static exception => exception is InvalidOperationException)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopRecognitionAsync().ConfigureAwait(false);
    }

    private void OnRecognizing(object? sender, SpeechRecognitionEventArgs eventArgs)
    {
        var text = eventArgs.Result.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            RecognitionUpdated?.Invoke(
                this,
                new SpeechRecognitionUpdate(
                    text,
                    IsFinal: false,
                    Error: null,
                    SegmentId: eventArgs.Result.ResultId));
        }
    }

    private void OnRecognized(object? sender, SpeechRecognitionEventArgs eventArgs)
    {
        if (eventArgs.Result.Reason != ResultReason.RecognizedSpeech)
        {
            return;
        }

        var text = eventArgs.Result.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            RecognitionUpdated?.Invoke(
                this,
                new SpeechRecognitionUpdate(
                    text,
                    IsFinal: true,
                    Error: null,
                    SegmentId: eventArgs.Result.ResultId));
        }
    }

    private void OnCanceled(object? sender, SpeechRecognitionCanceledEventArgs eventArgs)
    {
        var details = CancellationDetails.FromResult(eventArgs.Result);
        RecognitionUpdated?.Invoke(
            this,
            new SpeechRecognitionUpdate(
                Text: null,
                IsFinal: true,
                Error: $"Speech recognition canceled ({details.Reason}, {details.ErrorCode})."));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

/// <summary>
/// Creates Azure Speech pipelines using one shared endpoint and Entra credential.
/// </summary>
public sealed class AzureSpeechPipelineFactory : ISpeechPipelineFactory
{
    private readonly Uri _endpoint;
    private readonly TokenCredential _credential;

    /// <summary>
    /// Initializes a factory for one AI Services endpoint.
    /// </summary>
    public AzureSpeechPipelineFactory(Uri endpoint, TokenCredential credential)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
    }

    /// <inheritdoc />
    public ISpeechPipeline Create() => new AzureSpeechPipeline(_endpoint, _credential);
}
