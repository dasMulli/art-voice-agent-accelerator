using ServiceDeskCallSimulator.Speech;

namespace ServiceDeskCallSimulator.Tests;

public sealed class SpeechRecognitionLifecycleTests
{
    [Fact]
    public async Task FaultingNativeStop_ReleasesEveryOwnedResourceExactlyOnce()
    {
        var boundary = new FakeRecognitionNativeBoundary();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SpeechRecognitionLifecycle.StopAndDisposeAsync(
                () => Task.FromException(new InvalidOperationException("Native stop failed.")),
                boundary.ClosePushStream,
                boundary.DisposeRecognizer,
                boundary.DisposeAudioConfig,
                TimeSpan.FromSeconds(1),
                TimeProvider.System));

        boundary.AssertResourcesReleasedOnce();
    }

    [Fact]
    public async Task NeverCompletingNativeStop_ReleasesEveryOwnedResourceWithinTheDeadline()
    {
        var boundary = new FakeRecognitionNativeBoundary();

        var stopTask = SpeechRecognitionLifecycle.StopAndDisposeAsync(
            () => boundary.NeverCompletingStop.Task,
            boundary.ClosePushStream,
            boundary.DisposeRecognizer,
            boundary.DisposeAudioConfig,
            TimeSpan.FromMilliseconds(50),
            TimeProvider.System);

        await Assert.ThrowsAsync<TimeoutException>(() => stopTask)
            .WaitAsync(TimeSpan.FromSeconds(1));

        boundary.AssertResourcesReleasedOnce();
    }

    private sealed class FakeRecognitionNativeBoundary
    {
        public TaskCompletionSource NeverCompletingStop { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PushStreamCloseCalls { get; private set; }

        public int RecognizerDisposeCalls { get; private set; }

        public int AudioConfigDisposeCalls { get; private set; }

        public void ClosePushStream() => PushStreamCloseCalls++;

        public void DisposeRecognizer() => RecognizerDisposeCalls++;

        public void DisposeAudioConfig() => AudioConfigDisposeCalls++;

        public void AssertResourcesReleasedOnce()
        {
            Assert.Equal(1, PushStreamCloseCalls);
            Assert.Equal(1, RecognizerDisposeCalls);
            Assert.Equal(1, AudioConfigDisposeCalls);
        }
    }
}
