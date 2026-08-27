using System.Diagnostics;
using System.ComponentModel;
using System.Text;

namespace ServiceDeskCallSimulator.DevTunnel;

/// <summary>
/// Executes the installed Dev Tunnels CLI while bounding diagnostics and draining redirected streams.
/// </summary>
public sealed class SystemDevTunnelProcessRunner : IDevTunnelProcessRunner
{
    private const int MaximumDiagnosticsCharacters = 16 * 1024;
    private readonly string _executablePath;

    /// <summary>
    /// Initializes a process runner for the installed <c>devtunnel</c> executable.
    /// </summary>
    public SystemDevTunnelProcessRunner(string executablePath = "devtunnel")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
    }

    /// <inheritdoc />
    public bool IsExecutableAvailable()
    {
        if (Path.IsPathFullyQualified(_executablePath))
        {
            return File.Exists(_executablePath);
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];
        var hasExtension = Path.HasExtension(_executablePath);

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(directory => extensions.Any(extension =>
                File.Exists(Path.Combine(directory, hasExtension ? _executablePath : _executablePath + extension))));
    }

    /// <inheritdoc />
    public async Task<DevTunnelProcessResult> ExecuteAsync(
        DevTunnelCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        using var process = StartProcess(command);
        if (!command.CaptureDiagnostics)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return new DevTunnelProcessResult(process.ExitCode, string.Empty, string.Empty);
            }
            catch (OperationCanceledException)
            {
                await TerminateProcessAsync(process).ConfigureAwait(false);
                throw;
            }
        }

        var standardOutput = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        var standardError = ReadBoundedAsync(process.StandardError, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new DevTunnelProcessResult(
                process.ExitCode,
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            await TerminateProcessAsync(process).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<IDevTunnelHostProcess> StartHostAsync(DevTunnelCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IDevTunnelHostProcess>(new DevTunnelHostProcess(StartProcess(command)));
    }

    private Process StartProcess(DevTunnelCommand command)
    {
        var startInfo = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = command.CaptureDiagnostics,
            RedirectStandardError = command.CaptureDiagnostics,
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("The Dev Tunnels CLI process did not start.");
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                "Unable to start the Dev Tunnels CLI. Install it and ensure 'devtunnel' is on PATH.",
                exception);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var diagnostics = new BoundedDiagnostics();
        var buffer = new char[2048];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return diagnostics.Snapshot();
            }

            diagnostics.Append(buffer.AsSpan(0, read));
        }
    }

    private static async Task TerminateProcessAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    private sealed class DevTunnelHostProcess : IDevTunnelHostProcess
    {
        private readonly Process _process;
        private readonly BoundedDiagnostics _diagnostics = new();
        private readonly Task _standardOutput;
        private readonly Task _standardError;
        private readonly Task<int> _exitTask;

        public DevTunnelHostProcess(Process process)
        {
            _process = process;
            _standardOutput = DrainAsync(_process.StandardOutput);
            _standardError = DrainAsync(_process.StandardError);
            _exitTask = WaitForExitCoreAsync();
        }

        public bool HasExited => _process.HasExited;

        public string Diagnostics => _diagnostics.Snapshot();

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            return await _exitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> WaitForOutputAsync(
            Func<string, bool> predicate,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(predicate);

            while (true)
            {
                var diagnostics = Diagnostics;
                if (predicate(diagnostics))
                {
                    return diagnostics;
                }

                if (_exitTask.IsCompleted)
                {
                    var exitCode = await _exitTask.ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"The Dev Tunnels host process exited unexpectedly with code {exitCode}. {Diagnostics}");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task TerminateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            await WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                await TerminateAsync(CancellationToken.None).ConfigureAwait(false);
            }

            _process.Dispose();
        }

        private async Task DrainAsync(StreamReader reader)
        {
            var buffer = new char[2048];
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                _diagnostics.Append(buffer.AsSpan(0, read));
            }
        }

        private async Task<int> WaitForExitCoreAsync()
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(_standardOutput, _standardError).ConfigureAwait(false);
            return _process.ExitCode;
        }
    }

    private sealed class BoundedDiagnostics
    {
        private readonly StringBuilder _builder = new();
        private readonly object _gate = new();
        private bool _truncated;

        public void Append(ReadOnlySpan<char> value)
        {
            lock (_gate)
            {
                var remaining = MaximumDiagnosticsCharacters - _builder.Length;
                if (remaining <= 0)
                {
                    _truncated = true;
                    return;
                }

                _builder.Append(value[..Math.Min(remaining, value.Length)]);
                _truncated |= value.Length > remaining;
            }
        }

        public string Snapshot()
        {
            lock (_gate)
            {
                return _truncated
                    ? $"{_builder}\n[diagnostics truncated]"
                    : _builder.ToString();
            }
        }
    }
}
