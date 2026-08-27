using ServiceDeskCallSimulator.Monitoring;

namespace ServiceDeskCallSimulator.Tests;

public sealed class WaveOutAudioMonitorTests
{
    [Fact]
    public async Task BoundedQueueAndMute_AffectOnlyLocalWaveOutPlayback()
    {
        var native = new FakeWaveOutNative();
        await using var monitor = new WaveOutAudioMonitor(
            new WaveOutAudioMonitorOptions { MaximumBufferedFrames = 1 },
            native);

        monitor.IsMuted = true;
        Assert.True(monitor.TryMonitor(new byte[640]));
        await Task.Delay(30);
        Assert.Empty(native.Device.Written);

        monitor.IsMuted = false;
        Assert.True(monitor.TryMonitor(new byte[640]));
        await EventuallyAsync(() => native.Device.Written.Count == 1);
        Assert.False(monitor.TryMonitor(new byte[640]));

        native.Device.CompleteAll();
        await EventuallyAsync(() => native.Device.Unprepared.Count == 1);

        Assert.Equal(1, native.OpenCalls);
        Assert.Equal(16_000, native.Format!.SamplesPerSecond);
        Assert.Equal((short)16, native.Format.BitsPerSample);
        Assert.Equal((short)1, native.Format.Channels);
    }

    [Fact]
    public async Task StopAndDispose_ResetAndReleaseOnlyOwnedDeviceBuffers()
    {
        var native = new FakeWaveOutNative();
        var monitor = new WaveOutAudioMonitor(
            new WaveOutAudioMonitorOptions { MaximumBufferedFrames = 2 },
            native);

        Assert.True(monitor.TryMonitor(new byte[640]));
        await EventuallyAsync(() => native.Device.Written.Count == 1);

        await monitor.StopAsync();
        await monitor.DisposeAsync();

        Assert.Equal(1, native.Device.ResetCalls);
        Assert.Single(native.Device.Unprepared);
        Assert.True(native.Device.Disposed);
    }

    [Fact]
    public async Task Reservation_RetainsCapacityWhileADequeuedBufferBecomesInFlight()
    {
        var native = new FakeWaveOutNative();
        var dequeued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseDequeue = new ManualResetEventSlim();
        var monitor = new WaveOutAudioMonitor(
            new WaveOutAudioMonitorOptions { MaximumBufferedFrames = 1 },
            native,
            () =>
            {
                dequeued.TrySetResult();
                releaseDequeue.Wait();
            });

        try
        {
            Assert.True(monitor.TryMonitor(new byte[640]));
            await dequeued.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(monitor.TryMonitor(new byte[640]));

            releaseDequeue.Set();
            await EventuallyAsync(() => native.Device.Written.Count == 1);
        }
        finally
        {
            releaseDequeue.Set();
            await monitor.DisposeAsync();
        }
    }

    [Fact]
    public async Task WomDoneCallback_DefersUnprepareOffTheDeviceCallbackThread()
    {
        var native = new FakeWaveOutNative();
        await using var monitor = new WaveOutAudioMonitor(
            new WaveOutAudioMonitorOptions { MaximumBufferedFrames = 4 },
            native);

        Assert.True(monitor.TryMonitor(new byte[640]));
        await EventuallyAsync(() => native.Device.Written.Count == 1);

        native.Device.CompleteAll();
        await EventuallyAsync(() => native.Device.Unprepared.Count == 1);

        // The winmm callback contract forbids waveOutUnprepareHeader inside WOM_DONE.
        Assert.Empty(native.Device.UnpreparesInsideCallback);
    }

    [Fact]
    public async Task ResetDeliveredCallbacks_ReleaseEachBufferExactlyOnceAfterResetReturns()
    {
        var native = new FakeWaveOutNative();
        native.Device.CompleteWrittenBuffersDuringReset = true;
        var monitor = new WaveOutAudioMonitor(
            new WaveOutAudioMonitorOptions { MaximumBufferedFrames = 4 },
            native);

        Assert.True(monitor.TryMonitor(new byte[640]));
        Assert.True(monitor.TryMonitor(new byte[640]));
        await EventuallyAsync(() => native.Device.Written.Count == 2);

        await monitor.StopAsync();

        Assert.Equal(1, native.Device.ResetCalls);
        Assert.Equal(2, native.Device.Unprepared.Count);
        Assert.Equal(2, native.Device.Unprepared.Distinct().Count());
        Assert.Empty(native.Device.UnpreparesInsideCallback);
        Assert.Empty(native.Device.UnpreparesInsideReset);
        Assert.Equal(new[] { "reset", "unprepare", "unprepare" }, native.Device.Operations.ToArray());

        await monitor.DisposeAsync();
        Assert.Equal(2, native.Device.Unprepared.Count);
    }

    [Fact]
    public async Task RepeatedCompletionForOneBuffer_ReleasesItExactlyOnce()
    {
        var native = new FakeWaveOutNative();
        await using var monitor = new WaveOutAudioMonitor(
            new WaveOutAudioMonitorOptions { MaximumBufferedFrames = 2 },
            native);

        Assert.True(monitor.TryMonitor(new byte[640]));
        await EventuallyAsync(() => native.Device.Written.Count == 1);

        native.Device.CompleteAll();
        await EventuallyAsync(() => native.Device.Unprepared.Count == 1);
        native.Device.CompleteAll();
        await Task.Delay(50);

        Assert.Single(native.Device.Unprepared);

        // The reservation for the released buffer was returned exactly once.
        Assert.True(monitor.TryMonitor(new byte[640]));
        Assert.True(monitor.TryMonitor(new byte[640]));
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected asynchronous condition was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeWaveOutNative : IWaveOutNative
    {
        public int OpenCalls { get; private set; }

        public WaveOutFormat? Format { get; private set; }

        public FakeWaveOutDevice Device { get; } = new();

        public IWaveOutDevice Open(WaveOutFormat format, Action<WaveOutBuffer> completed)
        {
            OpenCalls++;
            Format = format;
            Device.Completed = completed;
            return Device;
        }
    }

    private sealed class FakeWaveOutDevice : IWaveOutDevice
    {
        [ThreadStatic]
        private static int _callbackDepth;

        [ThreadStatic]
        private static int _resetDepth;

        private readonly object _sync = new();

        public Action<WaveOutBuffer>? Completed { get; set; }

        public List<WaveOutBuffer> Written { get; } = [];

        public List<WaveOutBuffer> Unprepared { get; } = [];

        public List<WaveOutBuffer> UnpreparesInsideCallback { get; } = [];

        public List<WaveOutBuffer> UnpreparesInsideReset { get; } = [];

        public List<string> Operations { get; } = [];

        public bool CompleteWrittenBuffersDuringReset { get; set; }

        public int ResetCalls { get; private set; }

        public bool Disposed { get; private set; }

        public void PrepareAndWrite(WaveOutBuffer buffer)
        {
            lock (_sync)
            {
                Written.Add(buffer);
            }
        }

        public void Unprepare(WaveOutBuffer buffer)
        {
            // Reentrancy is measured per thread: a call that appears while this same thread is
            // inside WOM_DONE or waveOutReset is exactly the forbidden native-boundary violation.
            var insideCallback = _callbackDepth > 0;
            var insideReset = _resetDepth > 0;
            lock (_sync)
            {
                Unprepared.Add(buffer);
                Operations.Add("unprepare");
                if (insideCallback)
                {
                    UnpreparesInsideCallback.Add(buffer);
                }

                if (insideReset)
                {
                    UnpreparesInsideReset.Add(buffer);
                }
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                ResetCalls++;
                Operations.Add("reset");
            }

            if (!CompleteWrittenBuffersDuringReset)
            {
                return;
            }

            // winmm delivers WOM_DONE for every queued buffer while waveOutReset is executing.
            _resetDepth++;
            try
            {
                CompleteAll();
            }
            finally
            {
                _resetDepth--;
            }
        }

        public void Dispose() => Disposed = true;

        public void CompleteAll()
        {
            WaveOutBuffer[] buffers;
            lock (_sync)
            {
                buffers = Written.ToArray();
            }

            _callbackDepth++;
            try
            {
                foreach (var buffer in buffers)
                {
                    Completed?.Invoke(buffer);
                }
            }
            finally
            {
                _callbackDepth--;
            }
        }
    }
}
