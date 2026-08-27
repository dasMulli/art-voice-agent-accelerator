namespace ServiceDeskCallSimulator.Callback;

/// <summary>
/// Configures the local-only callback host.
/// </summary>
public sealed record CallbackHostOptions
{
    /// <summary>
    /// Gets the loopback port to bind. A value of zero requests an ephemeral port.
    /// </summary>
    public int Port { get; init; }

    /// <summary>
    /// Gets the maximum event payload retained for a registered handler.
    /// </summary>
    public int MaximumEventBodyBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// Gets the maximum time allowed for bounded host shutdown.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
