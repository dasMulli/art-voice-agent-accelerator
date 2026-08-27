using ServiceDeskCallSimulator.UI;

namespace ServiceDeskCallSimulator.Tests;

public sealed class ShutdownSequencerTests
{
    [Fact]
    public async Task RunAsync_RunsHangUpAndCallCleanupBeforeTunnelCleanup()
    {
        var order = new List<string>();

        var outcome = await ShutdownSequencer.RunAsync(
            async token =>
            {
                order.Add("hangup-start");
                await Task.Delay(10, token);
                order.Add("hangup-end");
            },
            async token =>
            {
                order.Add("tunnel-start");
                await Task.Delay(10, token);
                order.Add("tunnel-end");
            },
            TimeSpan.FromSeconds(5));

        Assert.Equal(ShutdownOutcome.Completed, outcome);
        Assert.Equal(["hangup-start", "hangup-end", "tunnel-start", "tunnel-end"], order);
    }

    [Fact]
    public async Task RunAsync_RunsExactTunnelCleanupWhenHangUpCleanupTimesOut()
    {
        const string ownedTunnelId = "sdcs-owned-tunnel";
        string? deletedTunnelId = null;

        var outcome = await ShutdownSequencer.RunAsync(
            async token =>
            {
                // Simulate a hang-up/call cleanup operation that never observes cancellation
                // promptly; the bound below must still make the sequence terminate.
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            async token =>
            {
                // This must receive a fresh timeout token rather than the already-cancelled
                // call-cleanup token.
                await Task.Delay(10, token);
                deletedTunnelId = ownedTunnelId;
            },
            TimeSpan.FromMilliseconds(50));

        Assert.Equal(ShutdownOutcome.TimedOut, outcome);
        Assert.Equal(ownedTunnelId, deletedTunnelId);
    }

    [Fact]
    public async Task RunAsync_EnforcesBoundAndThenRunsTunnelCleanupWhenCallCleanupIgnoresCancellation()
    {
        var tunnelCleanupCalled = false;

        var outcome = await ShutdownSequencer.RunAsync(
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task,
            _ =>
            {
                tunnelCleanupCalled = true;
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(50));

        Assert.Equal(ShutdownOutcome.TimedOut, outcome);
        Assert.True(tunnelCleanupCalled);
    }

    [Fact]
    public async Task RunAsync_ReturnsFailedWhenCleanupThrowsANonCancellationException()
    {
        var outcome = await ShutdownSequencer.RunAsync(
            _ => throw new InvalidOperationException("boom"),
            _ => Task.CompletedTask,
            TimeSpan.FromSeconds(5));

        Assert.Equal(ShutdownOutcome.Failed, outcome);
    }

    [Fact]
    public async Task RunAsync_CompletesWhenNeitherCleanupHasAnyWork()
    {
        var outcome = await ShutdownSequencer.RunAsync(
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            TimeSpan.FromSeconds(5));

        Assert.Equal(ShutdownOutcome.Completed, outcome);
    }

    [Fact]
    public async Task RunAsync_ExternalCancellationBeforeBoundStillStopsTheSequence()
    {
        using var externalCancellation = new CancellationTokenSource();
        externalCancellation.Cancel();

        var tunnelCleanupCalled = false;
        var outcome = await ShutdownSequencer.RunAsync(
            token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            _ =>
            {
                tunnelCleanupCalled = true;
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5),
            externalCancellation.Token);

        Assert.Equal(ShutdownOutcome.Failed, outcome);
        Assert.False(tunnelCleanupCalled);
    }
}
