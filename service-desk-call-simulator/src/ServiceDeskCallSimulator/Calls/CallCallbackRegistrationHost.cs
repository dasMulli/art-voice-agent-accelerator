using ServiceDeskCallSimulator.Callback;

namespace ServiceDeskCallSimulator.Calls;

/// <summary>
/// Exposes the already-started Dev Tunnel callback routes required by one call session.
/// </summary>
public interface ICallCallbackRegistrationHost
{
    /// <summary>
    /// Gets the public ACS CloudEvents callback route.
    /// </summary>
    Uri PublicEventUri { get; }

    /// <summary>
    /// Gets the public ACS media WebSocket route.
    /// </summary>
    Uri PublicMediaUri { get; }

    /// <summary>
    /// Registers the one active ACS connection ID with the shared callback host.
    /// </summary>
    IAsyncDisposable RegisterCall(
        string callConnectionId,
        CallbackEventHandler eventHandler,
        MediaConnectionHandler mediaHandler);
}
