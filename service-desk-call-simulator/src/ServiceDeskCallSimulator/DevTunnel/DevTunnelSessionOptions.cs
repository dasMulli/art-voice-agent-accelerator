namespace ServiceDeskCallSimulator.DevTunnel;

/// <summary>
/// Configures bounded Dev Tunnel startup and shutdown behavior.
/// </summary>
public sealed record DevTunnelSessionOptions
{
    /// <summary>
    /// Gets the maximum time allowed for a finite Dev Tunnels CLI command, including GitHub sign-in.
    /// </summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the maximum time to wait for the Dev Tunnels host to publish the callback port's
    /// public URI through <c>devtunnel show --json</c>.
    /// </summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the interval between <c>devtunnel show --json</c> polls during startup.
    /// </summary>
    public TimeSpan PortUriPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Gets the maximum time to wait for the exact host child process to exit.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
