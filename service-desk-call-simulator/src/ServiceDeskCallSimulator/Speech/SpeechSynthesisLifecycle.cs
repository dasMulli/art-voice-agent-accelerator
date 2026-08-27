namespace ServiceDeskCallSimulator.Speech;

/// <summary>
/// Coordinates bounded native synthesis cleanup after caller cancellation.
/// </summary>
internal static class SpeechSynthesisLifecycle
{
    internal static async Task<T> AwaitWithCancellationCleanupAsync<T>(
        Task<T> synthesisTask,
        Func<Task> stopSpeakingAsync,
        CancellationToken cancellationToken,
        TimeSpan cleanupTimeout,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(synthesisTask);
        ArgumentNullException.ThrowIfNull(stopSpeakingAsync);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (cleanupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cleanupTimeout),
                "The synthesis cleanup timeout must be positive.");
        }

        try
        {
            return await synthesisTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Task stopTask;
            try
            {
                stopTask = stopSpeakingAsync();
            }
            catch (Exception)
            {
                stopTask = Task.CompletedTask;
            }

            using var cleanupDeadline = new CancellationTokenSource(cleanupTimeout, timeProvider);
            var cleanupTask = Task.WhenAll(synthesisTask, stopTask);
            try
            {
                await cleanupTask.WaitAsync(cleanupDeadline.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _ = ObserveCompletionAsync(cleanupTask);
            }

            throw;
        }
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The caller cancellation has already been surfaced to the owner.
        }
    }
}
