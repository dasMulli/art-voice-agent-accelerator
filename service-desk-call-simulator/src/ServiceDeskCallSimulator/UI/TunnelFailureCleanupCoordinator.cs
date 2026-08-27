namespace ServiceDeskCallSimulator.UI;

/// <summary>
/// Serializes cleanup after an unexpected Dev Tunnel host exit. It accepts only the current
/// session, retires and finalizes its active call before disposing that session, and does not
/// release retry until all ordered cleanup callbacks have completed.
/// </summary>
public sealed class TunnelFailureCleanupCoordinator
{
    private readonly object _sync = new();
    private Task? _cleanupTask;

    /// <summary>
    /// Handles an unexpected host exit once for the current session. Stale events are ignored,
    /// and concurrent notifications for the same current session share one cleanup task.
    /// </summary>
    public Task HandleAsync(
        Func<bool> isStillCurrent,
        Func<Task<bool>> beginFailureAndRetireCallAsync,
        Func<Task> hangUpAndFinalizeCallAsync,
        Func<Task> disposeFailedSessionAsync,
        Func<Task> completeFailureCleanupAsync)
    {
        ArgumentNullException.ThrowIfNull(isStillCurrent);
        ArgumentNullException.ThrowIfNull(beginFailureAndRetireCallAsync);
        ArgumentNullException.ThrowIfNull(hangUpAndFinalizeCallAsync);
        ArgumentNullException.ThrowIfNull(disposeFailedSessionAsync);
        ArgumentNullException.ThrowIfNull(completeFailureCleanupAsync);

        lock (_sync)
        {
            if (!isStillCurrent())
            {
                return Task.CompletedTask;
            }

            if (_cleanupTask is not null)
            {
                return _cleanupTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanupTask = completion.Task;
            _cleanupTask = cleanupTask;
            _ = RunCleanupAsync(
                beginFailureAndRetireCallAsync,
                hangUpAndFinalizeCallAsync,
                disposeFailedSessionAsync,
                completeFailureCleanupAsync,
                completion);
            return cleanupTask;
        }
    }

    private async Task RunCleanupAsync(
        Func<Task<bool>> beginFailureAndRetireCallAsync,
        Func<Task> hangUpAndFinalizeCallAsync,
        Func<Task> disposeFailedSessionAsync,
        Func<Task> completeFailureCleanupAsync,
        TaskCompletionSource completion)
    {
        var cleanupStarted = false;
        Exception? failure = null;
        try
        {
            cleanupStarted = await beginFailureAndRetireCallAsync().ConfigureAwait(false);
            if (!cleanupStarted)
            {
                return;
            }

            try
            {
                await hangUpAndFinalizeCallAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            try
            {
                await disposeFailedSessionAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (cleanupStarted)
            {
                try
                {
                    await completeFailureCleanupAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }

            lock (_sync)
            {
                _cleanupTask = null;
            }

            if (failure is null)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(failure);
            }
        }
    }
}
