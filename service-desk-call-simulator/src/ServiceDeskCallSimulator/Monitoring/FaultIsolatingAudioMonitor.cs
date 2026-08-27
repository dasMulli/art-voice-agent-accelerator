namespace ServiceDeskCallSimulator.Monitoring;

/// <summary>
/// Owns one local monitor and atomically replaces it with a no-op monitor after its first
/// runtime device fault. Local playback is optional; ACS media delivery never depends on it.
/// </summary>
public sealed class FaultIsolatingAudioMonitor : IAudioMonitor
{
    private readonly object _sync = new();
    private IAudioMonitor _current;
    private Task? _failedMonitorCleanup;
    private int _disposed;

    /// <summary>
    /// Initializes an adapter around one per-call local audio monitor.
    /// </summary>
    public FaultIsolatingAudioMonitor(IAudioMonitor monitor)
    {
        _current = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _current.Faulted += OnInnerMonitorFaulted;
    }

    /// <inheritdoc />
    public bool IsMuted
    {
        get
        {
            lock (_sync)
            {
                return _current.IsMuted;
            }
        }
        set
        {
            lock (_sync)
            {
                _current.IsMuted = value;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<AudioMonitorFault>? Faulted;

    /// <inheritdoc />
    public bool TryMonitor(ReadOnlyMemory<byte> pcm16KMono)
    {
        IAudioMonitor monitor;
        lock (_sync)
        {
            monitor = _current;
        }

        try
        {
            return monitor.TryMonitor(pcm16KMono);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ObjectDisposedException
            or System.ComponentModel.Win32Exception)
        {
            DisableMonitor(monitor);
            return true;
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        IAudioMonitor monitor;
        Task? failedMonitorCleanup;
        lock (_sync)
        {
            monitor = _current;
            failedMonitorCleanup = _failedMonitorCleanup;
        }

        await monitor.StopAsync().ConfigureAwait(false);
        if (failedMonitorCleanup is not null)
        {
            await ObserveFailedMonitorCleanupAsync(failedMonitorCleanup).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        IAudioMonitor monitor;
        Task? failedMonitorCleanup;
        lock (_sync)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            monitor = _current;
            monitor.Faulted -= OnInnerMonitorFaulted;
            failedMonitorCleanup = _failedMonitorCleanup;
        }

        try
        {
            await monitor.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (failedMonitorCleanup is not null)
            {
                await ObserveFailedMonitorCleanupAsync(failedMonitorCleanup).ConfigureAwait(false);
            }
        }
    }

    private void OnInnerMonitorFaulted(object? sender, AudioMonitorFault fault)
    {
        if (sender is IAudioMonitor monitor)
        {
            DisableMonitor(monitor);
        }
    }

    private void DisableMonitor(IAudioMonitor monitor)
    {
        Task? cleanup;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0 || !ReferenceEquals(_current, monitor))
            {
                return;
            }

            var replacement = new NullAudioMonitor { IsMuted = monitor.IsMuted };
            monitor.Faulted -= OnInnerMonitorFaulted;
            _current = replacement;
            cleanup = _failedMonitorCleanup ??= StopAndDisposeFailedMonitorAsync(monitor);
        }

        try
        {
            Faulted?.Invoke(
                this,
                new AudioMonitorFault(
                    "disabled",
                    "Local audio playback was disabled after a device fault. The call continues."));
        }
        catch
        {
            // A UI diagnostic consumer must not be able to affect ACS media or the call.
        }

        _ = cleanup;
    }

    private static async Task StopAndDisposeFailedMonitorAsync(IAudioMonitor monitor)
    {
        try
        {
            await monitor.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // This monitor is already quarantined; continue to dispose its local resources.
        }

        try
        {
            await monitor.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Local playback cleanup cannot affect the active ACS call.
        }
    }

    private static async Task ObserveFailedMonitorCleanupAsync(Task cleanup)
    {
        try
        {
            await cleanup.ConfigureAwait(false);
        }
        catch
        {
            // StopAndDisposeFailedMonitorAsync isolates local cleanup failures by design.
        }
    }
}
