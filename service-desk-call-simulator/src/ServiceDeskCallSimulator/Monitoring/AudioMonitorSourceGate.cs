namespace ServiceDeskCallSimulator.Monitoring;

/// <summary>
/// Selects a single audio source for the local monitor FIFO so that inbound remote audio and
/// outbound caller playback never interleave in the same bounded queue.
/// </summary>
/// <remarks>
/// The monitor queue drains at one frame per playback period. Feeding it both directions at once
/// doubles the queue rate and garbles playback. This gate is a source selector only: inbound call
/// audio still reaches Speech recognition, and outbound audio still reaches ACS, unchanged.
/// </remarks>
public sealed class AudioMonitorSourceGate
{
    private int _activeOutboundPlaybacks;

    /// <summary>
    /// Gets whether caller playback currently owns the local monitor.
    /// </summary>
    public bool IsOutboundActive => Volatile.Read(ref _activeOutboundPlaybacks) > 0;

    /// <summary>
    /// Marks the start of one caller playback generation, giving it the monitor source.
    /// </summary>
    public void BeginOutbound() => Interlocked.Increment(ref _activeOutboundPlaybacks);

    /// <summary>
    /// Marks the end of one caller playback generation, returning the monitor to inbound audio.
    /// </summary>
    public void EndOutbound()
    {
        var remaining = Interlocked.Decrement(ref _activeOutboundPlaybacks);
        if (remaining < 0)
        {
            Interlocked.Increment(ref _activeOutboundPlaybacks);
            throw new InvalidOperationException(
                "The local monitor source gate ended more outbound playbacks than it began.");
        }
    }

    /// <summary>
    /// Returns whether one inbound remote frame should reach the local monitor. Silent comfort
    /// frames are never monitored, and remote audio is dropped while caller playback is active.
    /// </summary>
    public bool ShouldMonitorInbound(bool isSilent) => !isSilent && !IsOutboundActive;
}
