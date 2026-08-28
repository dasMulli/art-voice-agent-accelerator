using NAudio;
using NAudio.Wave;

namespace ServiceDeskCallSimulator.Monitoring;

/// <summary>
/// Options for one buffered NAudio waveOut monitor.
/// </summary>
public sealed record WaveOutAudioMonitorOptions
{
    /// <summary>
    /// Gets the waveOut device identifier, or -1 for the system default device.
    /// </summary>
    public int DeviceId { get; init; } = -1;

    /// <summary>
    /// Gets the maximum PCM frames retained by the local monitor.
    /// </summary>
    public int MaximumBufferedFrames { get; init; } = 50;

    /// <summary>
    /// Gets the portion of the bounded buffer reserved for outbound caller audio. When omitted,
    /// up to 20 percent of the default-sized buffer is reserved.
    /// </summary>
    public int? ReservedOutboundFrames { get; init; }

    /// <summary>
    /// Gets the duration of each NAudio waveOut device buffer.
    /// </summary>
    public int BufferMilliseconds { get; init; } = 40;

    /// <summary>
    /// Gets the number of native waveOut buffers used for continuous playback.
    /// </summary>
    public int NumberOfBuffers { get; init; } = 3;
}

/// <summary>
/// Bounded 16 kHz PCM local monitor backed by NAudio buffered playback.
/// </summary>
public sealed class WaveOutAudioMonitor : IAudioMonitor
{
    private readonly object _sync = new();
    private readonly IWavePlayer _player;
    private readonly BufferedWaveProvider _provider;
    private readonly int _inboundByteLimit;
    private readonly int _playbackStartByteCount;
    private int _muted;
    private int _started;
    private int _stopped;
    private int _disposed;

    /// <summary>
    /// Opens a buffered NAudio waveOut device for raw 16 kHz, 16-bit, mono PCM monitoring.
    /// </summary>
    public WaveOutAudioMonitor(WaveOutAudioMonitorOptions? options = null)
        : this(options ?? new WaveOutAudioMonitorOptions(), CreatePlayer(options))
    {
    }

    internal WaveOutAudioMonitor(
        WaveOutAudioMonitorOptions options,
        IWavePlayer player)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(player);
        ValidateOptions(options);

        _player = player;
        var reservedOutboundFrames = options.ReservedOutboundFrames
            ?? Math.Min(10, options.MaximumBufferedFrames / 5);
        _inboundByteLimit = (
            options.MaximumBufferedFrames
            - Math.Min(reservedOutboundFrames, options.MaximumBufferedFrames - 1))
            * AcsMediaFrameBytes;
        _provider = new BufferedWaveProvider(
            new WaveFormat(16_000, 16, 1),
            TimeSpan.FromMilliseconds(options.MaximumBufferedFrames * 20))
        {
            DiscardOnBufferOverflow = false,
            ReadFully = true,
        };
        _playbackStartByteCount = checked(
            _provider.WaveFormat.AverageBytesPerSecond
            * options.BufferMilliseconds
            * options.NumberOfBuffers
            / 1000);
        _player.PlaybackStopped += OnPlaybackStopped;
        try
        {
            _player.Init(_provider);
        }
        catch
        {
            _player.PlaybackStopped -= OnPlaybackStopped;
            _player.Dispose();
            throw;
        }
    }

    private const int AcsMediaFrameBytes = 640;

    /// <inheritdoc />
    public bool IsMuted
    {
        get => Volatile.Read(ref _muted) != 0;
        set => Volatile.Write(ref _muted, value ? 1 : 0);
    }

    /// <inheritdoc />
    public event EventHandler<AudioMonitorFault>? Faulted;

    /// <inheritdoc />
    public bool TryMonitor(ReadOnlyMemory<byte> pcm16KMono) =>
        TryMonitorCore(pcm16KMono, _inboundByteLimit);

    /// <inheritdoc />
    public bool TryMonitorOutbound(ReadOnlyMemory<byte> pcm16KMono) =>
        TryMonitorCore(pcm16KMono, _provider.BufferLength);

    /// <inheritdoc />
    public Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return Task.CompletedTask;
        }

        lock (_sync)
        {
            try
            {
                _player.Stop();
            }
            catch (Exception exception) when (exception is MmException
                or InvalidOperationException
                or ObjectDisposedException)
            {
                RaiseFault("reset", "The local NAudio output device could not be stopped.");
            }

            _provider.ClearBuffer();
            Volatile.Write(ref _started, 0);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _player.PlaybackStopped -= OnPlaybackStopped;
            _player.Dispose();
        }
    }

    private bool TryMonitorCore(ReadOnlyMemory<byte> pcm16KMono, int byteLimit)
    {
        if (pcm16KMono.IsEmpty || pcm16KMono.Length % sizeof(short) != 0)
        {
            throw new ArgumentException(
                "Local monitoring requires complete 16-bit PCM samples.",
                nameof(pcm16KMono));
        }

        if (IsMuted || Volatile.Read(ref _stopped) != 0)
        {
            return IsMuted;
        }

        lock (_sync)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                return false;
            }

            if (_provider.BufferedBytes + pcm16KMono.Length > byteLimit)
            {
                return false;
            }

            try
            {
                _provider.AddSamples(pcm16KMono.Span);
                if (Volatile.Read(ref _started) == 0
                    && _provider.BufferedBytes >= _playbackStartByteCount)
                {
                    _player.Play();
                    Volatile.Write(ref _started, 1);
                }
            }
            catch (Exception exception) when (exception is MmException
                or InvalidOperationException
                or ObjectDisposedException)
            {
                RaiseFault("write", "The local NAudio output device rejected PCM audio.");
                throw new InvalidOperationException(
                    "The local NAudio output device rejected PCM audio.",
                    exception);
            }
        }

        return true;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (eventArgs.Exception is not null && Volatile.Read(ref _stopped) == 0)
        {
            RaiseFault("playback", "The local NAudio output device stopped unexpectedly.");
        }
    }

    private void RaiseFault(string operation, string message) =>
        Faulted?.Invoke(this, new AudioMonitorFault(operation, message));

    private static IWavePlayer CreatePlayer(WaveOutAudioMonitorOptions? configuredOptions)
    {
        var options = configuredOptions ?? new WaveOutAudioMonitorOptions();
        ValidateOptions(options);
        return new WaveOut
        {
            DeviceNumber = options.DeviceId,
            BufferMilliseconds = options.BufferMilliseconds,
            NumberOfBuffers = options.NumberOfBuffers,
        };
    }

    private static void ValidateOptions(WaveOutAudioMonitorOptions options)
    {
        if (options.MaximumBufferedFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The local monitor buffer capacity must be positive.");
        }

        if (options.ReservedOutboundFrames < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The outbound monitor reservation cannot be negative.");
        }

        if (options.BufferMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The NAudio buffer duration must be positive.");
        }

        if (options.NumberOfBuffers < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "NAudio requires at least two output buffers.");
        }

        var providerMilliseconds = checked(options.MaximumBufferedFrames * 20);
        var nativeBufferMilliseconds = checked(
            options.BufferMilliseconds * options.NumberOfBuffers);
        if (nativeBufferMilliseconds > providerMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The NAudio output buffer window cannot exceed the bounded PCM provider.");
        }
    }
}

/// <summary>
/// Creates independently owned buffered NAudio monitors for caller conversations.
/// </summary>
public sealed class WaveOutAudioMonitorFactory : IAudioMonitorFactory
{
    private readonly WaveOutAudioMonitorOptions _options;

    /// <summary>
    /// Initializes a factory using the default waveOut device and bounded buffer settings.
    /// </summary>
    public WaveOutAudioMonitorFactory(WaveOutAudioMonitorOptions? options = null)
    {
        _options = options ?? new WaveOutAudioMonitorOptions();
    }

    /// <inheritdoc />
    public IAudioMonitor Create() => new WaveOutAudioMonitor(_options);
}
