using System.Runtime.InteropServices;

namespace ServiceDeskCallSimulator.Monitoring;

/// <summary>
/// Options for one local Windows waveOut monitor.
/// </summary>
public sealed record WaveOutAudioMonitorOptions
{
    /// <summary>
    /// Gets the waveOut device identifier, or -1 for the system default device.
    /// </summary>
    public int DeviceId { get; init; } = -1;

    /// <summary>
    /// Gets the maximum combined queued and native in-flight audio buffers.
    /// </summary>
    public int MaximumBufferedFrames { get; init; } = 50;
}

/// <summary>
/// Bounded 16 kHz PCM local monitor backed by a single owned Windows waveOut device.
/// </summary>
public sealed class WaveOutAudioMonitor : IAudioMonitor
{
    private readonly object _sync = new();
    private readonly WaveOutAudioMonitorOptions _options;
    private readonly IWaveOutDevice _device;
    private readonly Queue<byte[]> _queued = [];
    private readonly HashSet<WaveOutBuffer> _inFlight = [];
    private readonly Queue<WaveOutBuffer> _pendingRelease = [];
    private readonly SemaphoreSlim _queuedSignal = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private readonly Action? _afterDequeueForTesting;
    private int _muted;
    private int _reservedFrames;
    private int _stopped;
    private int _disposed;

    /// <summary>
    /// Opens a local waveOut device for raw 16 kHz, 16-bit, mono PCM monitoring.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// Raised when local waveOut monitoring is requested on a non-Windows platform.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised when the selected local output device cannot be opened.
    /// </exception>
    public WaveOutAudioMonitor(WaveOutAudioMonitorOptions? options = null)
        : this(options ?? new WaveOutAudioMonitorOptions(), new WinMmWaveOutNative())
    {
    }

    internal WaveOutAudioMonitor(
        WaveOutAudioMonitorOptions options,
        IWaveOutNative native,
        Action? afterDequeueForTesting = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(native);
        if (options.MaximumBufferedFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The local monitor buffer capacity must be positive.");
        }

        _options = options;
        _afterDequeueForTesting = afterDequeueForTesting;
        _device = native.Open(
            new WaveOutFormat(options.DeviceId, SamplesPerSecond: 16_000, BitsPerSample: 16, Channels: 1),
            OnBufferCompleted);
        _worker = Task.Run(ProcessQueueAsync);
    }

    /// <inheritdoc />
    public bool IsMuted
    {
        get => Volatile.Read(ref _muted) != 0;
        set => Volatile.Write(ref _muted, value ? 1 : 0);
    }

    /// <inheritdoc />
    public event EventHandler<AudioMonitorFault>? Faulted;

    /// <inheritdoc />
    public bool TryMonitor(ReadOnlyMemory<byte> pcm16KMono)
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

            if (_reservedFrames >= _options.MaximumBufferedFrames)
            {
                return false;
            }

            _reservedFrames++;
            _queued.Enqueue(pcm16KMono.ToArray());
        }

        _queuedSignal.Release();
        return true;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            await AwaitWorkerAsync().ConfigureAwait(false);
            return;
        }

        lock (_sync)
        {
            _reservedFrames -= _queued.Count;
            _queued.Clear();
        }

        _lifetime.Cancel();
        _queuedSignal.Release();
        try
        {
            _device.Reset();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ExternalException)
        {
            RaiseFault("reset", "The local waveOut device could not be reset.");
        }

        // The worker owns every waveOutUnprepareHeader call, so it must be finished before this
        // thread releases the buffers that Reset abandoned or that the device callback queued.
        await AwaitWorkerAsync().ConfigureAwait(false);
        ReleaseAllOutstandingBuffers();
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
            _device.Dispose();
            _lifetime.Dispose();
            _queuedSignal.Dispose();
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (true)
            {
                await _queuedSignal.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                ReleasePendingBuffers();
                byte[]? pcm;
                lock (_sync)
                {
                    pcm = _queued.Count > 0 && Volatile.Read(ref _stopped) == 0
                        ? _queued.Dequeue()
                        : null;
                }

                if (pcm is null)
                {
                    continue;
                }

                _afterDequeueForTesting?.Invoke();
                var buffer = new WaveOutBuffer(pcm);
                lock (_sync)
                {
                    if (Volatile.Read(ref _stopped) != 0)
                    {
                        _reservedFrames--;
                        buffer.Dispose();
                        continue;
                    }

                    _inFlight.Add(buffer);
                }

                try
                {
                    _device.PrepareAndWrite(buffer);
                }
                catch (Exception exception) when (exception is InvalidOperationException or ExternalException)
                {
                    RaiseFault("write", "The local waveOut device rejected an audio buffer.");
                    ReleaseInFlightBuffer(buffer);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // StopAsync owns local playback shutdown.
        }
    }

    /// <summary>
    /// Runs on the winmm WOM_DONE callback thread, where only enqueueing and signalling are legal.
    /// Calling waveOutUnprepareHeader here can deadlock, especially during waveOutReset.
    /// </summary>
    private void OnBufferCompleted(WaveOutBuffer buffer)
    {
        lock (_sync)
        {
            if (!_inFlight.Remove(buffer))
            {
                return;
            }

            _pendingRelease.Enqueue(buffer);
        }

        if (Volatile.Read(ref _stopped) != 0)
        {
            // StopAsync drains pending releases itself and may already have disposed the signal.
            return;
        }

        try
        {
            _queuedSignal.Release();
        }
        catch (ObjectDisposedException)
        {
            // A late device callback raced disposal; StopAsync already drained pending releases.
        }
    }

    private void ReleaseInFlightBuffer(WaveOutBuffer buffer)
    {
        bool owned;
        lock (_sync)
        {
            owned = _inFlight.Remove(buffer);
        }

        if (owned)
        {
            ReleaseClaimedBuffer(buffer);
        }
    }

    private void ReleasePendingBuffers()
    {
        while (true)
        {
            WaveOutBuffer buffer;
            lock (_sync)
            {
                if (_pendingRelease.Count == 0)
                {
                    return;
                }

                buffer = _pendingRelease.Dequeue();
            }

            ReleaseClaimedBuffer(buffer);
        }
    }

    private void ReleaseAllOutstandingBuffers()
    {
        while (true)
        {
            WaveOutBuffer buffer;
            lock (_sync)
            {
                if (_pendingRelease.Count > 0)
                {
                    buffer = _pendingRelease.Dequeue();
                }
                else if (_inFlight.Count > 0)
                {
                    buffer = _inFlight.First();
                    _inFlight.Remove(buffer);
                }
                else
                {
                    return;
                }
            }

            ReleaseClaimedBuffer(buffer);
        }
    }

    /// <summary>
    /// Releases one buffer that this thread exclusively claimed. Never call from a device callback.
    /// </summary>
    private void ReleaseClaimedBuffer(WaveOutBuffer buffer)
    {
        try
        {
            _device.Unprepare(buffer);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ExternalException)
        {
            RaiseFault("release", "The local waveOut buffer could not be released.");
        }
        finally
        {
            buffer.Dispose();
            lock (_sync)
            {
                _reservedFrames--;
            }
        }
    }

    private async Task AwaitWorkerAsync()
    {
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The worker uses cancellation as its normal stop signal.
        }
    }

    private void RaiseFault(string operation, string message) =>
        Faulted?.Invoke(this, new AudioMonitorFault(operation, message));
}

/// <summary>
/// Creates independently owned Windows waveOut monitors for caller conversations.
/// </summary>
public sealed class WaveOutAudioMonitorFactory : IAudioMonitorFactory
{
    private readonly WaveOutAudioMonitorOptions _options;

    /// <summary>
    /// Initializes a factory using the default waveOut device and bounded queue settings.
    /// </summary>
    public WaveOutAudioMonitorFactory(WaveOutAudioMonitorOptions? options = null)
    {
        _options = options ?? new WaveOutAudioMonitorOptions();
    }

    /// <inheritdoc />
    public IAudioMonitor Create() => new WaveOutAudioMonitor(_options);
}

internal sealed record WaveOutFormat(
    int DeviceId,
    int SamplesPerSecond,
    short BitsPerSample,
    short Channels);

internal interface IWaveOutNative
{
    IWaveOutDevice Open(WaveOutFormat format, Action<WaveOutBuffer> completed);
}

internal interface IWaveOutDevice : IDisposable
{
    void PrepareAndWrite(WaveOutBuffer buffer);

    void Unprepare(WaveOutBuffer buffer);

    void Reset();
}

internal sealed class WaveOutBuffer : IDisposable
{
    private GCHandle _pin;
    private int _disposed;

    public WaveOutBuffer(byte[] pcm)
    {
        Pcm = pcm ?? throw new ArgumentNullException(nameof(pcm));
        _pin = GCHandle.Alloc(pcm, GCHandleType.Pinned);
        Header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
        Marshal.StructureToPtr(
            new WaveHeader
            {
                Data = _pin.AddrOfPinnedObject(),
                BufferLength = checked((uint)pcm.Length),
            },
            Header,
            false);
    }

    public byte[] Pcm { get; }

    public nint Header { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Marshal.FreeHGlobal(Header);
        if (_pin.IsAllocated)
        {
            _pin.Free();
        }
    }
}

internal sealed class WinMmWaveOutNative : IWaveOutNative
{
    public IWaveOutDevice Open(WaveOutFormat format, Action<WaveOutBuffer> completed)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Local waveOut monitoring is supported only on Windows. Use NullAudioMonitor on this platform.");
        }

        return WinMmWaveOutDevice.Open(format, completed);
    }
}

internal sealed class WinMmWaveOutDevice : IWaveOutDevice
{
    private const uint CallbackFunction = 0x0003_0000;
    private const uint WaveMapper = uint.MaxValue;
    private const uint WaveMessageDone = 0x03BD;
    private readonly object _sync = new();
    private readonly Dictionary<nint, WaveOutBuffer> _buffers = [];
    private readonly Action<WaveOutBuffer> _completed;
    private readonly WaveOutProc _callback;
    private nint _handle;
    private int _disposed;

    private WinMmWaveOutDevice(nint handle, Action<WaveOutBuffer> completed, WaveOutProc callback)
    {
        _handle = handle;
        _completed = completed;
        _callback = callback;
    }

    public static WinMmWaveOutDevice Open(WaveOutFormat format, Action<WaveOutBuffer> completed)
    {
        nint? handle = null;
        WinMmWaveOutDevice? device = null;
        WaveOutProc callback = (_, message, _, header, _) =>
        {
            if (message == WaveMessageDone && device is not null)
            {
                device.OnBufferDone(header);
            }
        };
        var nativeFormat = new WaveFormatEx
        {
            FormatTag = 1,
            Channels = checked((ushort)format.Channels),
            SamplesPerSecond = checked((uint)format.SamplesPerSecond),
            BitsPerSample = checked((ushort)format.BitsPerSample),
            BlockAlign = checked((ushort)(format.Channels * (format.BitsPerSample / 8))),
            AverageBytesPerSecond = checked(
                (uint)(format.SamplesPerSecond * format.Channels * (format.BitsPerSample / 8))),
            ExtraSize = 0,
        };
        var result = waveOutOpen(
            out var openedHandle,
            format.DeviceId < 0 ? WaveMapper : checked((uint)format.DeviceId),
            in nativeFormat,
            callback,
            nint.Zero,
            CallbackFunction);
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"Unable to open the local waveOut device (winmm error {result}).");
        }

        handle = openedHandle;
        device = new WinMmWaveOutDevice(handle.Value, completed, callback);
        return device;
    }

    public void PrepareAndWrite(WaveOutBuffer buffer)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            ThrowIfDisposed();
            _buffers.Add(buffer.Header, buffer);
        }

        var headerSize = checked((uint)Marshal.SizeOf<WaveHeader>());
        var prepareResult = waveOutPrepareHeader(_handle, buffer.Header, headerSize);
        if (prepareResult != 0)
        {
            lock (_sync)
            {
                _buffers.Remove(buffer.Header);
            }

            throw new InvalidOperationException(
                $"Unable to prepare a local waveOut buffer (winmm error {prepareResult}).");
        }

        var writeResult = waveOutWrite(_handle, buffer.Header, headerSize);
        if (writeResult != 0)
        {
            _ = waveOutUnprepareHeader(_handle, buffer.Header, headerSize);
            lock (_sync)
            {
                _buffers.Remove(buffer.Header);
            }

            throw new InvalidOperationException(
                $"Unable to write a local waveOut buffer (winmm error {writeResult}).");
        }
    }

    public void Unprepare(WaveOutBuffer buffer)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_sync)
        {
            _buffers.Remove(buffer.Header);
        }

        var result = waveOutUnprepareHeader(
            _handle,
            buffer.Header,
            checked((uint)Marshal.SizeOf<WaveHeader>()));
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"Unable to release a local waveOut buffer (winmm error {result}).");
        }
    }

    public void Reset()
    {
        ThrowIfDisposed();
        var result = waveOutReset(_handle);
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"Unable to reset the local waveOut device (winmm error {result}).");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_handle != nint.Zero)
        {
            _ = waveOutClose(_handle);
            _handle = nint.Zero;
        }
    }

    private void OnBufferDone(nint header)
    {
        WaveOutBuffer? buffer;
        lock (_sync)
        {
            _buffers.TryGetValue(header, out buffer);
        }

        if (buffer is not null)
        {
            _completed(buffer);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WaveOutProc(
        nint deviceHandle,
        uint message,
        nint instance,
        nint parameter1,
        nint parameter2);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [DllImport("winmm.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint waveOutOpen(
        out nint waveOutHandle,
        uint deviceId,
        in WaveFormatEx format,
        WaveOutProc callback,
        nint instance,
        uint flags);

    [DllImport("winmm.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint waveOutPrepareHeader(nint waveOutHandle, nint header, uint headerSize);

    [DllImport("winmm.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint waveOutUnprepareHeader(nint waveOutHandle, nint header, uint headerSize);

    [DllImport("winmm.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint waveOutWrite(nint waveOutHandle, nint header, uint headerSize);

    [DllImport("winmm.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint waveOutReset(nint waveOutHandle);

    [DllImport("winmm.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint waveOutClose(nint waveOutHandle);
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaveHeader
{
    public nint Data;
    public uint BufferLength;
    public uint BytesRecorded;
    public nint User;
    public uint Flags;
    public uint Loops;
    public nint Next;
    public nint Reserved;
}
