using ServiceDeskCallSimulator.DevTunnel;

namespace ServiceDeskCallSimulator.UI;

/// <summary>
/// Provides an injectable, observed wait for a Dev Tunnel host failure.
/// </summary>
internal interface IDevTunnelSessionWatcher
{
    Task WatchAsync(
        DevTunnelSession session,
        Func<bool> isStillCurrent,
        Func<Task> onUnexpectedExitAsync,
        CancellationToken cancellationToken);
}

/// <summary>
/// Observes the exact Dev Tunnel session supplied by the form and suppresses stale callbacks.
/// </summary>
internal sealed class DevTunnelSessionWatcher : IDevTunnelSessionWatcher
{
    public async Task WatchAsync(
        DevTunnelSession session,
        Func<bool> isStillCurrent,
        Func<Task> onUnexpectedExitAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(isStillCurrent);
        ArgumentNullException.ThrowIfNull(onUnexpectedExitAsync);

        try
        {
            await session.UnexpectedHostExit.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Form shutdown owns cancellation of its lifetime watcher.
        }
        catch
        {
            if (!isStillCurrent())
            {
                return;
            }

            try
            {
                await onUnexpectedExitAsync().ConfigureAwait(false);
            }
            catch
            {
                // The form renders safe diagnostics inside its handler. Never leak a task fault.
            }
        }
    }
}
