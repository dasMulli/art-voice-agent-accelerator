namespace ServiceDeskCallSimulator.DevTunnel;

/// <summary>
/// Describes one Dev Tunnels CLI invocation without including credentials.
/// </summary>
public sealed record DevTunnelCommand(
    IReadOnlyList<string> Arguments,
    bool CaptureDiagnostics = true)
{
    /// <summary>
    /// Gets a display-safe command line. Credentials are deliberately unsupported.
    /// </summary>
    public string DisplayArguments => string.Join(
        " ",
        Arguments.Select(argument => argument.Contains(' ') ? $"\"{argument}\"" : argument));
}

/// <summary>
/// Holds bounded diagnostic output from a completed Dev Tunnels CLI invocation.
/// </summary>
public sealed record DevTunnelProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Represents the one long-lived <c>devtunnel host</c> process created for a session.
/// </summary>
public interface IDevTunnelHostProcess : IAsyncDisposable
{
    /// <summary>
    /// Gets whether the exact child process has exited.
    /// </summary>
    bool HasExited { get; }

    /// <summary>
    /// Gets bounded stdout and stderr diagnostics. This never contains credentials because commands never receive them.
    /// </summary>
    string Diagnostics { get; }

    /// <summary>
    /// Waits for the child process to exit.
    /// </summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Waits until bounded diagnostics satisfy the supplied predicate.
    /// </summary>
    Task<string> WaitForOutputAsync(Func<string, bool> predicate, CancellationToken cancellationToken);

    /// <summary>
    /// Terminates only this child process.
    /// </summary>
    Task TerminateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Runs Dev Tunnels CLI commands and enables deterministic unit tests through fake implementations.
/// </summary>
public interface IDevTunnelProcessRunner
{
    /// <summary>
    /// Checks whether the Dev Tunnels CLI executable can be started.
    /// </summary>
    bool IsExecutableAvailable();

    /// <summary>
    /// Runs a finite CLI command.
    /// </summary>
    Task<DevTunnelProcessResult> ExecuteAsync(DevTunnelCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Starts the one long-lived hosting command.
    /// </summary>
    Task<IDevTunnelHostProcess> StartHostAsync(DevTunnelCommand command, CancellationToken cancellationToken);
}
