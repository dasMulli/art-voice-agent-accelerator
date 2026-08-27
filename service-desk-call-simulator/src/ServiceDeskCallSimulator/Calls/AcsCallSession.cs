using Azure;
using Azure.Communication.CallAutomation;
using ServiceDeskCallSimulator.Callback;
using ServiceDeskCallSimulator.Media;
using ServiceDeskCallSimulator.Validation;

namespace ServiceDeskCallSimulator.Calls;

/// <summary>
/// Specifies timeout values for one outbound ACS call session.
/// </summary>
public sealed record AcsCallSessionOptions
{
    /// <summary>
    /// Gets the maximum time allowed for the full dialing operation, including create and connection.
    /// </summary>
    public TimeSpan DialTimeout { get; init; } = TimeSpan.FromSeconds(40);

    /// <summary>
    /// Gets the maximum time allowed for one cleanup or hang-up operation.
    /// </summary>
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Represents media-streaming status independently from the call connection lifecycle.
/// </summary>
public enum AcsMediaSessionState
{
    NotStarted,
    Started,
    Stopped,
    Failed,
}

/// <summary>
/// Describes one immutable media-streaming status transition.
/// </summary>
public sealed record AcsMediaStateChange(
    AcsMediaSessionState PreviousState,
    AcsMediaSessionState CurrentState,
    DateTimeOffset Timestamp,
    string Reason);

/// <summary>
/// Owns one outbound PSTN call, its callback registration, and its bidirectional media transport.
/// </summary>
public sealed class AcsCallSession : IOwnedCallerCallSession
{
    private readonly ICallCallbackRegistrationHost _callbackHost;
    private readonly ICallAutomationGateway _gateway;
    private readonly AcsCallSessionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _startupSync = new();
    private readonly object _hangUpSync = new();
    private readonly object _cleanupSync = new();
    private readonly object _disposeSync = new();
    private readonly object _registrationSync = new();
    private readonly object _mediaStateSync = new();
    private readonly CancellationTokenSource _sessionCancellation = new();
    private readonly TaskCompletionSource _connected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ICallConnectionHandle?> _connectionAvailable =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IAsyncDisposable? _registration;
    private ICallConnectionHandle? _connection;
    private Task? _startupTask;
    private Task? _createEventTask;
    private Task? _dialTimeoutTask;
    private Task? _hangUpTask;
    private Task? _cleanupTask;
    private Task? _disposeTask;
    private AcsMediaSessionState _mediaState = AcsMediaSessionState.NotStarted;
    private int _disposeRequested;
    private int _primitivesDisposed;

    /// <summary>
    /// Initializes a session against an already-started Dev Tunnel callback lifecycle.
    /// </summary>
    public AcsCallSession(
        ICallCallbackRegistrationHost callbackHost,
        ICallAutomationGateway gateway,
        AcsMediaTransport? mediaTransport = null,
        AcsCallSessionOptions? options = null,
        TimeProvider? timeProvider = null)
        : this(callbackHost, gateway, mediaTransport, options, timeProvider, stateMachine: null)
    {
    }

    internal AcsCallSession(
        ICallCallbackRegistrationHost callbackHost,
        ICallAutomationGateway gateway,
        CallSessionStateMachine stateMachine,
        AcsCallSessionOptions? options = null,
        TimeProvider? timeProvider = null)
        : this(callbackHost, gateway, mediaTransport: null, options, timeProvider, stateMachine)
    {
    }

    private AcsCallSession(
        ICallCallbackRegistrationHost callbackHost,
        ICallAutomationGateway gateway,
        AcsMediaTransport? mediaTransport,
        AcsCallSessionOptions? options,
        TimeProvider? timeProvider,
        CallSessionStateMachine? stateMachine)
    {
        _callbackHost = callbackHost ?? throw new ArgumentNullException(nameof(callbackHost));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _options = options ?? new AcsCallSessionOptions();
        ValidateOptions(_options);
        _timeProvider = timeProvider ?? TimeProvider.System;
        MediaTransport = mediaTransport ?? new AcsMediaTransport();
        StateMachine = stateMachine ?? new CallSessionStateMachine(_timeProvider);
    }

    /// <summary>
    /// Gets the atomic call-lifecycle state machine.
    /// </summary>
    public CallSessionStateMachine StateMachine { get; }

    /// <summary>
    /// Completes after ACS connects the outbound call.
    /// </summary>
    public Task ConnectionReady => _connected.Task;

    /// <summary>
    /// Gets the current call state.
    /// </summary>
    public CallSessionState State => StateMachine.State;

    /// <summary>
    /// Gets the active ACS connection ID after a create-call response.
    /// </summary>
    public string? CallConnectionId { get; private set; }

    /// <summary>
    /// Gets the Task 4-ready bidirectional PCM transport for this call.
    /// </summary>
    public AcsMediaTransport MediaTransport { get; }

    ICallMediaTransport ICallerCallSession.CallerMediaTransport => MediaTransport;

    /// <summary>
    /// Raised for immutable ACS call state transitions.
    /// </summary>
    public event EventHandler<CallStateChange>? StateChanged
    {
        add => StateMachine.StateChanged += value;
        remove => StateMachine.StateChanged -= value;
    }

    /// <summary>
    /// Gets the current media-streaming status.
    /// </summary>
    public AcsMediaSessionState MediaState
    {
        get
        {
            lock (_mediaStateSync)
            {
                return _mediaState;
            }
        }
    }

    /// <summary>
    /// Raised synchronously with immutable media streaming status details.
    /// </summary>
    public event EventHandler<AcsMediaStateChange>? MediaStateChanged;

    /// <summary>
    /// Starts exactly one outbound PSTN call using source and destination E.164 phone numbers.
    /// </summary>
    public Task StartAsync(
        string sourcePhoneNumber,
        string destinationPhoneNumber,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposalRequested();
        E164PhoneNumber.EnsureValid(sourcePhoneNumber, nameof(sourcePhoneNumber));
        E164PhoneNumber.EnsureValid(destinationPhoneNumber, nameof(destinationPhoneNumber));

        lock (_startupSync)
        {
            ThrowIfDisposalRequested();
            if (_startupTask is not null)
            {
                throw new InvalidOperationException("A call session can have only one active call.");
            }

            _startupTask = StartCoreAsync(sourcePhoneNumber, destinationPhoneNumber, cancellationToken);
            return _startupTask;
        }
    }

    /// <summary>
    /// Requests one shared P2P hang-up and safely cleans up the active call resources.
    /// </summary>
    public Task HangUpAsync(CancellationToken cancellationToken = default)
    {
        var hangUpTask = BeginHangUp("Manual hang-up requested.");
        return AwaitHangUpAsync(hangUpTask, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeSync)
        {
            if (_disposeTask is null)
            {
                lock (_registrationSync)
                {
                    Interlocked.Exchange(ref _disposeRequested, 1);
                    _sessionCancellation.Cancel();
                }

                Task? startupTask;
                lock (_startupSync)
                {
                    startupTask = _startupTask;
                }

                if (startupTask is null)
                {
                    _connectionAvailable.TrySetResult(null);
                }

                _ = BeginHangUp("Session disposal requested.");
                _disposeTask = DisposeCoreAsync(startupTask);
            }

            disposeTask = _disposeTask;
        }

        return new ValueTask(AwaitCleanupDeadlineAsync(disposeTask));
    }

    private async Task StartCoreAsync(
        string sourcePhoneNumber,
        string destinationPhoneNumber,
        CancellationToken callerCancellationToken)
    {
        var acquiredLifecycleGate = false;
        try
        {
            await _lifecycleGate.WaitAsync(callerCancellationToken).ConfigureAwait(false);
            acquiredLifecycleGate = true;
            if (IsStopping)
            {
                throw new OperationCanceledException(_sessionCancellation.Token);
            }

            StateMachine.TransitionTo(CallSessionState.Dialing, "Outbound call requested.");
            var createRequest = new AcsCreateCallRequest(
                sourcePhoneNumber,
                destinationPhoneNumber,
                _callbackHost.PublicEventUri,
                new AcsMediaStreamingRequest(
                    _callbackHost.PublicMediaUri,
                    StartMediaStreaming: true,
                    EnableBidirectional: true,
                    EnableDtmfTones: false));

            var dialDeadline = _timeProvider.GetUtcNow() + _options.DialTimeout;
            using var dialDeadlineCancellation = new CancellationTokenSource(
                _options.DialTimeout,
                _timeProvider);
            using var createCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellationToken,
                _sessionCancellation.Token,
                dialDeadlineCancellation.Token);

            var creation = await _gateway.CreateCallAsync(createRequest, createCancellation.Token)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(creation.CallConnectionId))
            {
                StateMachine.TryTransitionTo(CallSessionState.Faulted, "ACS returned no call connection ID.", out _);
                _sessionCancellation.Cancel();
                throw new InvalidOperationException("ACS returned no call connection ID.");
            }

            var connection = _gateway.GetCallConnection(creation.CallConnectionId);
            _connection = connection;
            CallConnectionId = creation.CallConnectionId;
            _connectionAvailable.TrySetResult(connection);

            if (createCancellation.IsCancellationRequested || IsStopping)
            {
                StateMachine.TryTransitionTo(
                    CallSessionState.Faulted,
                    "Create call completed after session cancellation.",
                    out _);
                await BeginHangUp(
                    "Create call completed after session cancellation.",
                    preserveFaultedState: State == CallSessionState.Faulted).ConfigureAwait(false);
                createCancellation.Token.ThrowIfCancellationRequested();
            }

            // The create response event processor begins before callbacks are registered. It closes
            // the tiny create-response/callback-registration window without admitting unknown IDs.
            _createEventTask = ObserveInitialEventAsync(creation, _sessionCancellation.Token);
            lock (_registrationSync)
            {
                if (IsStopping)
                {
                    throw new OperationCanceledException(_sessionCancellation.Token);
                }

                _registration = _callbackHost.RegisterCall(
                    creation.CallConnectionId,
                    HandleCallbackAsync,
                    HandleMediaConnectionAsync);
            }

            _dialTimeoutTask = ObserveDialTimeoutAsync(
                dialDeadline - _timeProvider.GetUtcNow(),
                _sessionCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StateMachine.TryTransitionTo(CallSessionState.Faulted, "Create call was cancelled.", out _);
            _sessionCancellation.Cancel();
            await BeginCleanup().ConfigureAwait(false);
            throw;
        }
        catch (RequestFailedException)
        {
            StateMachine.TryTransitionTo(CallSessionState.Faulted, "ACS rejected the create call request.", out _);
            _sessionCancellation.Cancel();
            await BeginCleanup().ConfigureAwait(false);
            throw;
        }
        catch (InvalidOperationException)
        {
            StateMachine.TryTransitionTo(
                CallSessionState.Faulted,
                "The callback registration could not be established.",
                out _);
            _sessionCancellation.Cancel();
            await BeginCleanup().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _connectionAvailable.TrySetResult(_connection);
            if (acquiredLifecycleGate)
            {
                _lifecycleGate.Release();
            }
        }
    }

    private Task HandleCallbackAsync(CallbackEvent callbackEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callbackEvent);
        cancellationToken.ThrowIfCancellationRequested();

        var events = CallAutomationEventParser.ParseMany(BinaryData.FromBytes(callbackEvent.Body));
        foreach (var callEvent in events)
        {
            if (!string.Equals(callEvent.CallConnectionId, callbackEvent.CallConnectionId, StringComparison.Ordinal))
            {
                continue;
            }

            switch (callEvent)
            {
                case CallConnected:
                    StateMachine.TryTransitionTo(CallSessionState.Connected, "ACS reported CallConnected.", out _);
                    _connected.TrySetResult();
                    break;
                case CallDisconnected:
                    StateMachine.TryTransitionTo(CallSessionState.Ended, "ACS reported CallDisconnected.", out _);
                    _sessionCancellation.Cancel();
                    _ = BeginCleanup();
                    break;
                case CreateCallFailed:
                    StateMachine.TryTransitionTo(CallSessionState.Faulted, "ACS reported CreateCallFailed.", out _);
                    _sessionCancellation.Cancel();
                    _ = BeginCleanup();
                    break;
                case MediaStreamingStarted:
                    TransitionMediaState(AcsMediaSessionState.Started, "ACS reported MediaStreamingStarted.");
                    break;
                case MediaStreamingStopped:
                    TransitionMediaState(AcsMediaSessionState.Stopped, "ACS reported MediaStreamingStopped.");
                    break;
                case MediaStreamingFailed:
                    TransitionMediaState(AcsMediaSessionState.Failed, "ACS reported MediaStreamingFailed.");
                    StateMachine.TryTransitionTo(CallSessionState.Faulted, "ACS media streaming failed.", out _);
                    _sessionCancellation.Cancel();
                    _ = BeginCleanup();
                    break;
            }
        }

        return Task.CompletedTask;
    }

    private async Task HandleMediaConnectionAsync(MediaConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            await MediaTransport.HandleConnectionAsync(connection.WebSocket, cancellationToken).ConfigureAwait(false);
        }
        catch (AcsMediaProtocolException)
        {
            StateMachine.TryTransitionTo(CallSessionState.Faulted, "ACS media protocol validation failed.", out _);
            _sessionCancellation.Cancel();
            _ = BeginCleanup();
            throw;
        }
    }

    private async Task ObserveInitialEventAsync(AcsCallCreation creation, CancellationToken cancellationToken)
    {
        try
        {
            var initialEvent = await creation.WaitForInitialEventAsync(cancellationToken).ConfigureAwait(false);
            if (initialEvent.IsSuccess)
            {
                StateMachine.TryTransitionTo(
                    CallSessionState.Connected,
                    "ACS create-call event processor reported CallConnected.",
                    out _);
                _connected.TrySetResult();
                return;
            }

            StateMachine.TryTransitionTo(
                CallSessionState.Faulted,
                initialEvent.FailureReason ?? "ACS create-call event processor reported failure.",
                out _);
            _sessionCancellation.Cancel();
            await BeginCleanup().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_sessionCancellation.IsCancellationRequested)
        {
            // Session completion intentionally cancels the event processor wait.
        }
        catch (RequestFailedException)
        {
            StateMachine.TryTransitionTo(CallSessionState.Faulted, "ACS event processor failed.", out _);
            _sessionCancellation.Cancel();
            await BeginHangUp("ACS event processor failed.", preserveFaultedState: true).ConfigureAwait(false);
        }
    }

    private async Task ObserveDialTimeoutAsync(TimeSpan remainingDialTime, CancellationToken cancellationToken)
    {
        if (remainingDialTime <= TimeSpan.Zero)
        {
            await HandleDialTimeoutAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await _connected.Task.WaitAsync(remainingDialTime, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await HandleDialTimeoutAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Call completion intentionally ends the timeout monitor.
        }
    }

    private async Task HandleDialTimeoutAsync()
    {
        var transitionedToFaulted = StateMachine.TryTransitionTo(
            CallSessionState.Faulted,
            "Timed out waiting for CallConnected.",
            out _);
        if (!transitionedToFaulted && State == CallSessionState.Ended)
        {
            return;
        }

        _sessionCancellation.Cancel();
        await BeginHangUp(
            "Timed out waiting for CallConnected.",
            preserveFaultedState: true).ConfigureAwait(false);
    }

    private Task BeginHangUp(string reason, bool preserveFaultedState = false)
    {
        lock (_hangUpSync)
        {
            if (_hangUpTask is not null)
            {
                return _hangUpTask;
            }

            Task? startupTask;
            lock (_startupSync)
            {
                startupTask = _startupTask;
            }

            if (State == CallSessionState.Ended
                || (State == CallSessionState.Idle && startupTask is null))
            {
                return Task.CompletedTask;
            }

            var preserveFaultedStateForThisRequest =
                preserveFaultedState && State == CallSessionState.Faulted;
            if (!preserveFaultedStateForThisRequest)
            {
                StateMachine.TryTransitionTo(CallSessionState.Ending, reason, out _);
            }

            _sessionCancellation.Cancel();
            _hangUpTask = HangUpCoreAsync(preserveFaultedStateForThisRequest);
            return _hangUpTask;
        }
    }

    private async Task HangUpCoreAsync(bool preserveFaultedState)
    {
        try
        {
            var connection = await _connectionAvailable.Task.ConfigureAwait(false);
            if (connection is null)
            {
                if (!preserveFaultedState)
                {
                    StateMachine.TryTransitionTo(
                        CallSessionState.Ended,
                        "Call creation ended before an ACS connection was available.",
                        out _);
                }

                return;
            }

            using var cleanupTimeout = new CancellationTokenSource(_options.CleanupTimeout);
            try
            {
                await connection.HangUpAsync(true, cleanupTimeout.Token)
                    .WaitAsync(cleanupTimeout.Token)
                    .ConfigureAwait(false);
                if (!preserveFaultedState)
                {
                    StateMachine.TryTransitionTo(CallSessionState.Ended, "ACS hang-up completed.", out _);
                }
            }
            catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested)
            {
                var transitionedToFaulted = StateMachine.TryTransitionTo(
                    CallSessionState.Faulted,
                    "ACS hang-up timed out.",
                    out _);
                if (!transitionedToFaulted && State == CallSessionState.Ended)
                {
                    return;
                }

                throw;
            }
            catch (RequestFailedException)
            {
                var transitionedToFaulted = StateMachine.TryTransitionTo(
                    CallSessionState.Faulted,
                    "ACS hang-up request failed.",
                    out _);
                if (!transitionedToFaulted && State == CallSessionState.Ended)
                {
                    return;
                }

                throw;
            }
        }
        finally
        {
            await BeginCleanup().ConfigureAwait(false);
        }
    }

    private Task BeginCleanup()
    {
        lock (_cleanupSync)
        {
            return _cleanupTask ??= CleanupAsync();
        }
    }

    private async Task CleanupAsync()
    {
        _sessionCancellation.Cancel();
        using var cleanupTimeout = new CancellationTokenSource(_options.CleanupTimeout);
        try
        {
            await MediaTransport.CloseAsync(cleanupTimeout.Token).ConfigureAwait(false);
            IAsyncDisposable? registration;
            lock (_registrationSync)
            {
                registration = _registration;
                _registration = null;
            }

            if (registration is not null)
            {
                await registration.DisposeAsync().AsTask().WaitAsync(cleanupTimeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested)
        {
            StateMachine.TryTransitionTo(CallSessionState.Faulted, "Session cleanup timed out.", out _);
        }
    }

    private async Task DisposeCoreAsync(Task? startupTask)
    {
        try
        {
            if (startupTask is not null)
            {
                try
                {
                    await startupTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_sessionCancellation.IsCancellationRequested)
                {
                    // Disposal cancels the in-flight startup operation.
                }
                catch (RequestFailedException)
                {
                    // The starting caller receives the ACS failure; disposal still releases resources.
                }
                catch (InvalidOperationException)
                {
                    // The starting caller receives an invalid create or registration response.
                }
            }

            Task? hangUpTask;
            lock (_hangUpSync)
            {
                hangUpTask = _hangUpTask;
            }

            if (hangUpTask is not null)
            {
                try
                {
                    await hangUpTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The cleanup deadline bounds the outstanding ACS operation.
                }
                catch (RequestFailedException)
                {
                    // The state already records the failed ACS hang-up request.
                }
            }

            await BeginCleanup().ConfigureAwait(false);
            await AwaitBackgroundTaskAsync(_createEventTask).ConfigureAwait(false);
            await AwaitBackgroundTaskAsync(_dialTimeoutTask).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _primitivesDisposed, 1);
            await MediaTransport.DisposeAsync().ConfigureAwait(false);
            _sessionCancellation.Dispose();
            _lifecycleGate.Dispose();
        }
    }

    private async Task AwaitHangUpAsync(Task hangUpTask, CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled)
        {
            await hangUpTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await AwaitCleanupDeadlineAsync(hangUpTask).ConfigureAwait(false);
    }

    private async Task AwaitCleanupDeadlineAsync(Task task)
    {
        using var cleanupTimeout = new CancellationTokenSource(_options.CleanupTimeout);
        await task.WaitAsync(cleanupTimeout.Token).ConfigureAwait(false);
    }

    private static async Task AwaitBackgroundTaskAsync(Task? task)
    {
        if (task is not null)
        {
            await task.ConfigureAwait(false);
        }
    }

    private void TransitionMediaState(AcsMediaSessionState nextState, string reason)
    {
        lock (_mediaStateSync)
        {
            if (_mediaState == nextState)
            {
                return;
            }

            var change = new AcsMediaStateChange(_mediaState, nextState, _timeProvider.GetUtcNow(), reason);
            _mediaState = nextState;
            MediaStateChanged?.Invoke(this, change);
        }
    }

    private static void ValidateOptions(AcsCallSessionOptions options)
    {
        if (options.DialTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The dial timeout must be positive.");
        }

        if (options.CleanupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The cleanup timeout must be positive.");
        }
    }

    private bool IsStopping =>
        Volatile.Read(ref _disposeRequested) != 0 || _sessionCancellation.IsCancellationRequested;

    private void ThrowIfDisposalRequested()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeRequested) != 0 || Volatile.Read(ref _primitivesDisposed) != 0,
            this);
    }
}
