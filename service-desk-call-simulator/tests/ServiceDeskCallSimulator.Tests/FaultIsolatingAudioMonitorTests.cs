using ServiceDeskCallSimulator.Monitoring;

namespace ServiceDeskCallSimulator.Tests;

public sealed class FaultIsolatingAudioMonitorTests
{
    [Fact]
    public async Task RuntimeFaultAfterConstruction_SwapsToNoOpAndEmitsOneSafeDiagnostic()
    {
        var inner = new FakeAudioMonitor();
        await using var monitor = new FaultIsolatingAudioMonitor(inner);
        var faults = new List<AudioMonitorFault>();
        monitor.Faulted += (_, fault) => faults.Add(fault);
        monitor.IsMuted = true;

        inner.RaiseFault("write", "raw device error that must not reach diagnostics");
        await EventuallyAsync(() => inner.Disposed);
        inner.RaiseFault("write", "another raw error");

        Assert.Single(faults);
        Assert.Equal("disabled", faults[0].Operation);
        Assert.Equal(
            "Local audio playback was disabled after a device fault. The call continues.",
            faults[0].Message);
        Assert.True(monitor.IsMuted);
        Assert.True(monitor.TryMonitor(new byte[640]));
        Assert.Empty(inner.Frames);
        Assert.Equal(1, inner.StopCalls);
        Assert.Equal(1, inner.DisposeCalls);
    }

    [Fact]
    public async Task RuntimeTryMonitorFailure_SwapsToNoOpWithoutThrowingIntoTheCall()
    {
        var inner = new FakeAudioMonitor { ThrowOnTryMonitor = true };
        await using var monitor = new FaultIsolatingAudioMonitor(inner);
        var faults = new List<AudioMonitorFault>();
        monitor.Faulted += (_, fault) => faults.Add(fault);

        Assert.True(monitor.TryMonitor(new byte[640]));
        await EventuallyAsync(() => inner.Disposed);

        Assert.Single(faults);
        Assert.Equal("disabled", faults[0].Operation);
        Assert.True(monitor.TryMonitor(new byte[640]));
    }

    [Fact]
    public async Task RuntimeFault_DoesNotRunDeviceCleanupInlineOnTheCallbackThread()
    {
        using var releaseStop = new ManualResetEventSlim();
        var inner = new FakeAudioMonitor { StopBlocker = releaseStop };
        await using var monitor = new FaultIsolatingAudioMonitor(inner);

        var callback = Task.Run(() => inner.RaiseFault("playback", "device callback failed"));
        try
        {
            var completed = await Task.WhenAny(callback, Task.Delay(500));
            Assert.Same(callback, completed);
        }
        finally
        {
            releaseStop.Set();
            await callback;
        }

        await EventuallyAsync(() => inner.Disposed);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The expected local monitor cleanup did not occur.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeAudioMonitor : IAudioMonitor
    {
        public bool IsMuted { get; set; }

        public bool ThrowOnTryMonitor { get; init; }

        public ManualResetEventSlim? StopBlocker { get; init; }

        public List<byte[]> Frames { get; } = [];

        public int StopCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public bool Disposed { get; private set; }

        public event EventHandler<AudioMonitorFault>? Faulted;

        public bool TryMonitor(ReadOnlyMemory<byte> pcm16KMono)
        {
            if (ThrowOnTryMonitor)
            {
                throw new InvalidOperationException("device failure");
            }

            Frames.Add(pcm16KMono.ToArray());
            return true;
        }

        public Task StopAsync()
        {
            StopCalls++;
            StopBlocker?.Wait();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public void RaiseFault(string operation, string message) =>
            Faulted?.Invoke(this, new AudioMonitorFault(operation, message));
    }
}
