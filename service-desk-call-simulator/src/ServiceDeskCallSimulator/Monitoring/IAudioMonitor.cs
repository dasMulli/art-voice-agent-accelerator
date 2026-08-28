namespace ServiceDeskCallSimulator.Monitoring;

/// <summary>
/// Reports a local-monitoring failure without exposing call audio.
/// </summary>
public sealed record AudioMonitorFault(string Operation, string Message);

/// <summary>
/// Provides bounded local PCM monitoring independent of ACS media delivery.
/// </summary>
public interface IAudioMonitor : IAsyncDisposable
{
    /// <summary>
    /// Gets or sets whether local speaker playback is muted.
    /// </summary>
    bool IsMuted { get; set; }

    /// <summary>
    /// Raised when local device playback fails; calls continue independently.
    /// </summary>
    event EventHandler<AudioMonitorFault>? Faulted;

    /// <summary>
    /// Attempts to queue raw 16 kHz, 16-bit, mono PCM for local playback.
    /// </summary>
    bool TryMonitor(ReadOnlyMemory<byte> pcm16KMono);

    /// <summary>
    /// Attempts to queue outbound caller PCM using capacity reserved from inbound monitoring.
    /// </summary>
    bool TryMonitorOutbound(ReadOnlyMemory<byte> pcm16KMono) => TryMonitor(pcm16KMono);

    /// <summary>
    /// Stops and resets the local device, discarding only local playback buffers.
    /// </summary>
    Task StopAsync();
}

/// <summary>
/// Creates independently owned local monitors for individual calls.
/// </summary>
public interface IAudioMonitorFactory
{
    /// <summary>
    /// Creates a monitor for a single caller conversation.
    /// </summary>
    IAudioMonitor Create();
}

/// <summary>
/// A monitor used by tests and callers that intentionally disable local playback.
/// </summary>
public sealed class NullAudioMonitor : IAudioMonitor
{
    /// <inheritdoc />
    public bool IsMuted { get; set; }

    /// <inheritdoc />
    public event EventHandler<AudioMonitorFault>? Faulted
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public bool TryMonitor(ReadOnlyMemory<byte> pcm16KMono) => true;

    /// <inheritdoc />
    public Task StopAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
