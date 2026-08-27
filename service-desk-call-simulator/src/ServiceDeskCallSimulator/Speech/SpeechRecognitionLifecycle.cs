using System.Runtime.ExceptionServices;

namespace ServiceDeskCallSimulator.Speech;

/// <summary>
/// Coordinates bounded native recognition shutdown and resource release.
/// </summary>
internal static class SpeechRecognitionLifecycle
{
    internal static async Task StopAndDisposeAsync(
        Func<Task>? stopRecognitionAsync,
        Action? closePushStream,
        Action? disposeRecognizer,
        Action? disposeAudioConfig,
        TimeSpan cleanupTimeout,
        TimeProvider timeProvider,
        Func<Exception, bool>? isExpectedStopFailure = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (cleanupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cleanupTimeout),
                "The recognition cleanup timeout must be positive.");
        }

        Exception? failure = null;
        Task? stopTask = null;
        try
        {
            if (stopRecognitionAsync is not null)
            {
                try
                {
                    stopTask = stopRecognitionAsync();
                    await stopTask.WaitAsync(cleanupTimeout, timeProvider).ConfigureAwait(false);
                }
                catch (Exception exception) when (isExpectedStopFailure?.Invoke(exception) == true)
                {
                    // The native recognizer can already be stopped after a cancellation event.
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }
        }
        finally
        {
            CaptureFailure(ref failure, closePushStream);
            CaptureFailure(ref failure, disposeRecognizer);
            CaptureFailure(ref failure, disposeAudioConfig);
        }

        if (stopTask is { IsCompleted: false })
        {
            _ = ObserveCompletionAsync(stopTask);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void CaptureFailure(ref Exception? failure, Action? action)
    {
        if (action is null)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
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
            // The bounded cleanup failure was already returned to the lifecycle owner.
        }
    }
}
