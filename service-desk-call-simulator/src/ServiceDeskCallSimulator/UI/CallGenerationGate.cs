namespace ServiceDeskCallSimulator.UI;

/// <summary>
/// Assigns a monotonically increasing generation ID to each call attempt so worker events
/// raised by a previous, already-torn-down call's resources can be safely ignored after a
/// new call starts or per-call teardown completes.
/// </summary>
public sealed class CallGenerationGate
{
    private long _current;

    /// <summary>
    /// Gets the generation ID of the currently active call, or zero when no call is active.
    /// </summary>
    public long Current => Interlocked.Read(ref _current);

    /// <summary>
    /// Advances to a new generation and returns it. Call once per new call attempt.
    /// </summary>
    public long Advance() => Interlocked.Increment(ref _current);

    /// <summary>
    /// Retires the active call's generation without starting a new one, so any further
    /// event carrying the retired generation is ignored.
    /// </summary>
    public void Retire(long generation)
    {
        Interlocked.CompareExchange(ref _current, 0, generation);
    }

    /// <summary>
    /// Returns whether the supplied generation is still the active one.
    /// </summary>
    public bool IsCurrent(long generation) => Interlocked.Read(ref _current) == generation;
}
