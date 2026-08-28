using NAudio.Wave;
using ServiceDeskCallSimulator.Media;
using ServiceDeskCallSimulator.Monitoring;

namespace ServiceDeskCallSimulator.Tests;

public sealed class WaveOutAudioMonitorTests
{
    [Fact]
    public async Task BufferedPlayback_UsesBoundedPcmProvider()
    {
        var player = new FakeWavePlayer();
        await using var monitor = new WaveOutAudioMonitor(
            new WaveOutAudioMonitorOptions
            {
                MaximumBufferedFrames = 3,
                BufferMilliseconds = 20,
                NumberOfBuffers = 2,
            },
            player);

        var provider = Assert.IsType<BufferedWaveProvider>(player.Provider);
        Assert.Equal(16_000, provider.WaveFormat.SampleRate);
        Assert.Equal(16, provider.WaveFormat.BitsPerSample);
        Assert.Equal(1, provider.WaveFormat.Channels);
        Assert.Equal(AcsMediaTransport.PcmFrameBytes * 3, provider.BufferLength);
        Assert.True(provider.ReadFully);
        Assert.False(provider.DiscardOnBufferOverflow);

        Assert.True(monitor.TryMonitor(new byte[AcsMediaTransport.PcmFrameBytes]));
        Assert.Equal(0, player.PlayCalls);
        Assert.True(monitor.TryMonitor(new byte[AcsMediaTransport.PcmFrameBytes]));
        Assert.Equal(1, player.PlayCalls);
        Assert.Equal(AcsMediaTransport.PcmFrameBytes * 2, provider.BufferedBytes);

        await monitor.StopAsync();

        Assert.Equal(1, player.StopCalls);
        Assert.Equal(0, provider.BufferedBytes);
    }

    [Fact]
    public async Task BoundedBufferAndMute_AffectOnlyLocalPlayback()
    {
        var player = new FakeWavePlayer();
        await using var monitor = new WaveOutAudioMonitor(
            new WaveOutAudioMonitorOptions
            {
                MaximumBufferedFrames = 2,
                BufferMilliseconds = 20,
                NumberOfBuffers = 2,
            },
            player);
        var provider = Assert.IsType<BufferedWaveProvider>(player.Provider);

        monitor.IsMuted = true;
        Assert.True(monitor.TryMonitor(new byte[AcsMediaTransport.PcmFrameBytes]));
        Assert.Equal(0, provider.BufferedBytes);
        Assert.Equal(0, player.PlayCalls);

        monitor.IsMuted = false;
        Assert.True(monitor.TryMonitor(new byte[AcsMediaTransport.PcmFrameBytes]));
        Assert.True(monitor.TryMonitor(new byte[AcsMediaTransport.PcmFrameBytes]));
        Assert.False(monitor.TryMonitor(new byte[AcsMediaTransport.PcmFrameBytes]));
        Assert.Equal(1, player.PlayCalls);
    }

    [Fact]
    public async Task OutboundReservation_PreventsInboundAudioFromConsumingTheWholeBuffer()
    {
        var player = new FakeWavePlayer();
        await using var monitor = new WaveOutAudioMonitor(
            new WaveOutAudioMonitorOptions
            {
                MaximumBufferedFrames = 3,
                ReservedOutboundFrames = 1,
                BufferMilliseconds = 20,
                NumberOfBuffers = 2,
            },
            player);

        Assert.True(monitor.TryMonitor(new byte[AcsMediaTransport.PcmFrameBytes]));
        Assert.True(monitor.TryMonitor(new byte[AcsMediaTransport.PcmFrameBytes]));
        Assert.False(monitor.TryMonitor(new byte[AcsMediaTransport.PcmFrameBytes]));

        Assert.True(monitor.TryMonitorOutbound(new byte[AcsMediaTransport.PcmFrameBytes]));
        Assert.False(monitor.TryMonitorOutbound(new byte[AcsMediaTransport.PcmFrameBytes]));
    }

    [Fact]
    public async Task UnexpectedPlaybackFailure_RaisesOneSafeFault()
    {
        var player = new FakeWavePlayer();
        await using var monitor = new WaveOutAudioMonitor(
            new WaveOutAudioMonitorOptions(),
            player);
        var faults = new List<AudioMonitorFault>();
        monitor.Faulted += (_, fault) => faults.Add(fault);

        player.RaisePlaybackStopped(new InvalidOperationException("driver details"));

        var fault = Assert.Single(faults);
        Assert.Equal("playback", fault.Operation);
        Assert.Equal("The local NAudio output device stopped unexpectedly.", fault.Message);
    }

    [Fact]
    public async Task StopAndDispose_AreIdempotent()
    {
        var player = new FakeWavePlayer();
        var monitor = new WaveOutAudioMonitor(
            new WaveOutAudioMonitorOptions(),
            player);

        Assert.True(monitor.TryMonitor(new byte[AcsMediaTransport.PcmFrameBytes]));
        await monitor.StopAsync();
        await monitor.StopAsync();
        await monitor.DisposeAsync();
        await monitor.DisposeAsync();

        Assert.Equal(1, player.StopCalls);
        Assert.Equal(1, player.DisposeCalls);
    }

    [Fact]
    public void InitializationFailure_DisposesTheOwnedPlayer()
    {
        var player = new FakeWavePlayer { InitException = new InvalidOperationException("init failed") };

        Assert.Throws<InvalidOperationException>(() =>
            new WaveOutAudioMonitor(new WaveOutAudioMonitorOptions(), player));

        Assert.Equal(1, player.DisposeCalls);
    }

    [Fact]
    public void NativeBufferWindowLargerThanProvider_IsRejected()
    {
        var options = new WaveOutAudioMonitorOptions
        {
            MaximumBufferedFrames = 3,
            BufferMilliseconds = 40,
            NumberOfBuffers = 2,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WaveOutAudioMonitor(options, new FakeWavePlayer()));
    }

    [Theory]
    [InlineData(0, 100, 3)]
    [InlineData(1, 0, 3)]
    [InlineData(1, 100, 1)]
    public void InvalidBufferOptions_AreRejected(
        int maximumBufferedFrames,
        int bufferMilliseconds,
        int numberOfBuffers)
    {
        var options = new WaveOutAudioMonitorOptions
        {
            MaximumBufferedFrames = maximumBufferedFrames,
            BufferMilliseconds = bufferMilliseconds,
            NumberOfBuffers = numberOfBuffers,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WaveOutAudioMonitor(options, new FakeWavePlayer()));
    }

    private sealed class FakeWavePlayer : IWavePlayer
    {
        public IWaveProvider? Provider { get; private set; }

        public int PlayCalls { get; private set; }

        public int StopCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public Exception? InitException { get; init; }

        public float Volume { get; set; } = 1.0f;

        public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;

        public WaveFormat OutputWaveFormat => Provider?.WaveFormat ?? new WaveFormat(16_000, 16, 1);

        public event EventHandler<StoppedEventArgs>? PlaybackStopped;

        public void Init(IWaveProvider waveProvider)
        {
            if (InitException is not null)
            {
                throw InitException;
            }

            Provider = waveProvider;
        }

        public void Play()
        {
            PlayCalls++;
            PlaybackState = PlaybackState.Playing;
        }

        public void Pause() => PlaybackState = PlaybackState.Paused;

        public void Stop()
        {
            StopCalls++;
            PlaybackState = PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs());
        }

        public void Dispose() => DisposeCalls++;

        public void RaisePlaybackStopped(Exception exception)
        {
            PlaybackState = PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs(exception));
        }
    }
}
