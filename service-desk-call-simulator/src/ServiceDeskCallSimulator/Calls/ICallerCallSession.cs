using ServiceDeskCallSimulator.Media;

namespace ServiceDeskCallSimulator.Calls;

/// <summary>
/// Narrow call-session lifecycle boundary used by the scripted caller.
/// </summary>
public interface ICallerCallSession
{
    /// <summary>
    /// Completes after ACS connects the outbound call.
    /// </summary>
    Task ConnectionReady { get; }

    /// <summary>
    /// Gets the current ACS call state.
    /// </summary>
    CallSessionState State { get; }

    /// <summary>
    /// Gets the call's bidirectional PCM transport.
    /// </summary>
    ICallMediaTransport CallerMediaTransport { get; }

    /// <summary>
    /// Raised for immutable ACS call state transitions.
    /// </summary>
    event EventHandler<CallStateChange>? StateChanged;

    /// <summary>
    /// Ends the call once, irrespective of concurrent callers.
    /// </summary>
    Task HangUpAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A caller call session that also owns disposable per-call resources.
/// </summary>
public interface IOwnedCallerCallSession : ICallerCallSession, IAsyncDisposable
{
}
