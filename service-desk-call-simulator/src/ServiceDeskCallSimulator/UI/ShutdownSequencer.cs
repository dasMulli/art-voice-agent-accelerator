namespace ServiceDeskCallSimulator.UI;

/// <summary>
/// The result of one bounded, ordered shutdown sequence.
/// </summary>
public enum ShutdownOutcome
{
    Completed,
    TimedOut,
    Failed,
}

/// <summary>
/// Orders bounded shutdown work so an active call's hang-up and per-call cleanup always
/// complete (or are abandoned by the bound) before the session's Dev Tunnel/callback host is
/// stopped and deleted. WinForms-independent so it can be exercised with fakes.
/// </summary>
public static class ShutdownSequencer
{
    /// <summary>
    /// Runs <paramref name="hangUpAndCallCleanupAsync"/> to completion or a bounded abandonment
    /// decision, then runs <paramref name="tunnelCleanupAsync"/> under its own bound. An external
    /// cancellation request stops the sequence before tunnel cleanup; an internal timeout does
    /// not leave the exact owned tunnel behind.
    /// </summary>
    public static async Task<ShutdownOutcome> RunAsync(
        Func<CancellationToken, Task> hangUpAndCallCleanupAsync,
        Func<CancellationToken, Task> tunnelCleanupAsync,
        TimeSpan bound,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hangUpAndCallCleanupAsync);
        ArgumentNullException.ThrowIfNull(tunnelCleanupAsync);

        var callOutcome = await RunBoundedAsync(
            hangUpAndCallCleanupAsync,
            bound,
            cancellationToken).ConfigureAwait(false);

        if (callOutcome == BoundedOperationOutcome.Cancelled)
        {
            return ShutdownOutcome.Failed;
        }

        // Call cleanup has either completed, failed, or been explicitly abandoned because it
        // exceeded its bound. In all three cases, continue with cleanup of this application's
        // exact Dev Tunnel under a fresh, independent bound.
        var tunnelOutcome = await RunBoundedAsync(
            tunnelCleanupAsync,
            bound,
            cancellationToken).ConfigureAwait(false);

        if (tunnelOutcome == BoundedOperationOutcome.Cancelled)
        {
            return ShutdownOutcome.Failed;
        }

        if (callOutcome == BoundedOperationOutcome.TimedOut
            || tunnelOutcome == BoundedOperationOutcome.TimedOut)
        {
            return ShutdownOutcome.TimedOut;
        }

        return callOutcome == BoundedOperationOutcome.Completed
            && tunnelOutcome == BoundedOperationOutcome.Completed
            ? ShutdownOutcome.Completed
            : ShutdownOutcome.Failed;
    }

    private static async Task<BoundedOperationOutcome> RunBoundedAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan bound,
        CancellationToken externalCancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(bound);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            externalCancellationToken,
            timeoutSource.Token);

        Task operationTask;
        try
        {
            operationTask = operation(linkedSource.Token);
        }
        catch
        {
            return BoundedOperationOutcome.Failed;
        }

        try
        {
            await operationTask.WaitAsync(linkedSource.Token).ConfigureAwait(false);
            return BoundedOperationOutcome.Completed;
        }
        catch (OperationCanceledException) when (externalCancellationToken.IsCancellationRequested)
        {
            ObserveLateCompletion(operationTask);
            return BoundedOperationOutcome.Cancelled;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            ObserveLateCompletion(operationTask);
            return BoundedOperationOutcome.TimedOut;
        }
        catch
        {
            return BoundedOperationOutcome.Failed;
        }
    }

    private static void ObserveLateCompletion(Task operationTask)
    {
        _ = operationTask.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private enum BoundedOperationOutcome
    {
        Completed,
        TimedOut,
        Failed,
        Cancelled,
    }
}
