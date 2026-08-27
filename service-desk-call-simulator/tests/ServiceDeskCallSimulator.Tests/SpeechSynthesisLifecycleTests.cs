using ServiceDeskCallSimulator.Speech;

namespace ServiceDeskCallSimulator.Tests;

public sealed class SpeechSynthesisLifecycleTests
{
    [Fact]
    public async Task Cancellation_AwaitsStopAndSynthesisCompletionBeforeReturning()
    {
        using var cancellation = new CancellationTokenSource();
        var synthesis = new TaskCompletionSource<byte[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowStopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = SpeechSynthesisLifecycle.AwaitWithCancellationCleanupAsync(
            synthesis.Task,
            () =>
            {
                stopStarted.TrySetResult();
                return allowStopCompletion.Task;
            },
            cancellation.Token,
            TimeSpan.FromSeconds(1),
            TimeProvider.System);

        cancellation.Cancel();
        await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(operation.IsCompleted);

        synthesis.TrySetResult([1, 2]);
        allowStopCompletion.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task Cancellation_UsesTheBoundedCleanupDeadlineWhenNativeWorkDoesNotFinish()
    {
        using var cancellation = new CancellationTokenSource();
        var synthesis = new TaskCompletionSource<byte[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = SpeechSynthesisLifecycle.AwaitWithCancellationCleanupAsync(
            synthesis.Task,
            () =>
            {
                stopStarted.TrySetResult();
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            },
            cancellation.Token,
            TimeSpan.FromMilliseconds(50),
            TimeProvider.System);

        cancellation.Cancel();

        await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }
}
