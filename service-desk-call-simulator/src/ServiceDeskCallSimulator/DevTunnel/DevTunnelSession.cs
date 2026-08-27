using System.Security.Cryptography;
using ServiceDeskCallSimulator.Callback;
using ServiceDeskCallSimulator.Calls;

namespace ServiceDeskCallSimulator.DevTunnel;

/// <summary>
/// Owns one local callback host and its exact temporary anonymous Dev Tunnel lifecycle.
/// </summary>
public sealed class DevTunnelSession : IAsyncDisposable, ICallCallbackRegistrationHost
{
    private readonly CallbackHost _callbackHost;
    private readonly IDevTunnelProcessRunner _processRunner;
    private readonly DevTunnelSessionOptions _options;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private IDevTunnelHostProcess? _hostProcess;
    private string? _tunnelId;
    private Task? _unexpectedHostExit;
    private bool _stopping;
    private bool _hasStopped;
    private bool _disposed;

    /// <summary>
    /// Initializes a session that owns the specified callback host.
    /// </summary>
    public DevTunnelSession(
        CallbackHost callbackHost,
        IDevTunnelProcessRunner? processRunner = null,
        DevTunnelSessionOptions? options = null)
    {
        _callbackHost = callbackHost ?? throw new ArgumentNullException(nameof(callbackHost));
        _processRunner = processRunner ?? new SystemDevTunnelProcessRunner();
        _options = options ?? new DevTunnelSessionOptions();
        ValidateOptions(_options);
    }

    /// <summary>
    /// Gets the local callback host owned by this session.
    /// </summary>
    public CallbackHost CallbackHost => _callbackHost;

    /// <summary>
    /// Gets the tunnel ID created only for this session after startup.
    /// </summary>
    public string TunnelId => _tunnelId ?? throw new InvalidOperationException("The Dev Tunnel session has not started.");

    /// <summary>
    /// Gets the derived public event callback URI after startup.
    /// </summary>
    public Uri PublicEventUri { get; private set; } = null!;

    /// <summary>
    /// Gets the derived public media WebSocket URI after startup.
    /// </summary>
    public Uri PublicMediaUri { get; private set; } = null!;

    /// <summary>
    /// Completes with an exception if the long-lived Dev Tunnels host process exits unexpectedly.
    /// </summary>
    public Task UnexpectedHostExit => _unexpectedHostExit ?? Task.CompletedTask;

    /// <summary>
    /// Gets whether this session still owns a tunnel that must be deleted before it is discarded.
    /// </summary>
    public bool HasRetainedTunnel => _tunnelId is not null;

    /// <summary>
    /// Raised once the local callback host has bound its loopback port during startup.
    /// A UI layer may use this to progress a "local callback host" checklist stage.
    /// </summary>
    public event EventHandler? CallbackHostStarted;

    /// <summary>
    /// Raised immediately before invoking interactive Dev Tunnels GitHub sign-in
    /// (<c>devtunnel user login -g</c>). A UI layer may use this to show a
    /// "sign-in required" status while the browser flow is in progress.
    /// </summary>
    public event EventHandler? SignInRequired;

    /// <inheritdoc />
    public IAsyncDisposable RegisterCall(
        string callConnectionId,
        CallbackEventHandler eventHandler,
        MediaConnectionHandler mediaHandler) =>
        _callbackHost.RegisterCall(callConnectionId, eventHandler, mediaHandler);

    /// <summary>
    /// Starts the local callback host and temporary Dev Tunnel without blocking the UI thread.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_hasStopped)
            {
                throw new InvalidOperationException("A stopped Dev Tunnel session cannot be restarted. Create a new session.");
            }

            if (_hostProcess is not null)
            {
                ThrowIfHostExited();
                return;
            }

            if (!_processRunner.IsExecutableAvailable())
            {
                throw new InvalidOperationException(
                    "The Dev Tunnels CLI was not found. Install it and ensure 'devtunnel' is on PATH.");
            }

            await _callbackHost.StartAsync(cancellationToken).ConfigureAwait(false);
            CallbackHostStarted?.Invoke(this, EventArgs.Empty);
            try
            {
                await EnsureSignedInAsync(cancellationToken).ConfigureAwait(false);
                var tunnelIdCandidate = CreateTunnelId();
                await ExecuteRequiredAsync(
                    new DevTunnelCommand(["create", tunnelIdCandidate, "--allow-anonymous", "--json"]),
                    "create the Dev Tunnel",
                    cancellationToken).ConfigureAwait(false);
                _tunnelId = tunnelIdCandidate;

                await ExecuteRequiredAsync(
                    new DevTunnelCommand(
                        ["port", "create", _tunnelId, "--port-number", _callbackHost.BoundPort.ToString(), "--protocol", "http", "--json"]),
                    "add the callback port to the Dev Tunnel",
                    cancellationToken).ConfigureAwait(false);

                // The host command must carry no port or access mutation flags. Passing
                // '--port-number/--protocol/--allow-anonymous' to 'devtunnel host' on an existing
                // tunnel fails with "Batch update of ports is not supported"; the port and the
                // anonymous access were already configured by 'create' and 'port create'.
                _hostProcess = await _processRunner.StartHostAsync(
                    new DevTunnelCommand(["host", _tunnelId]),
                    cancellationToken).ConfigureAwait(false);
                _unexpectedHostExit = MonitorHostExitAsync(_hostProcess);
                ObserveTaskFault(_unexpectedHostExit);

                // The public URI of the forwarded port only exists once the host is connected, and
                // only 'show --json' reports it.
                var publicEndpoint = await WaitForPublicPortUriAsync(
                    _callbackHost.BoundPort,
                    cancellationToken).ConfigureAwait(false);

                PublicEventUri = _callbackHost.Routes.BuildEventUri(publicEndpoint);
                PublicMediaUri = _callbackHost.Routes.BuildMediaUri(publicEndpoint);
                ThrowIfHostExited();
            }
            catch (Exception startupFailure)
            {
                await CleanupAfterFailedStartAsync().ConfigureAwait(false);
                throw new InvalidOperationException("Unable to start the callback Dev Tunnel session.", startupFailure);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Stops only the session host child process, deletes only the session tunnel, and then stops local callbacks.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var disposePrimitives = false;
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            // Do not mark the session disposed until StopCoreAsync has deleted the exact owned
            // tunnel. A failed delete must remain retryable rather than orphaning ownership.
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            _disposed = true;
            disposePrimitives = true;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (disposePrimitives)
        {
            _lifecycleGate.Dispose();
            await _callbackHost.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task EnsureSignedInAsync(CancellationToken cancellationToken)
    {
        var showResult = await ExecuteCommandAsync(
            new DevTunnelCommand(["user", "show", "--json"]),
            cancellationToken).ConfigureAwait(false);
        if (showResult.ExitCode == 0 && !RequiresSignIn(showResult))
        {
            return;
        }

        if (!RequiresSignIn(showResult))
        {
            throw CommandFailure("check Dev Tunnels authentication", showResult);
        }

        SignInRequired?.Invoke(this, EventArgs.Empty);
        var loginResult = await ExecuteCommandAsync(
            new DevTunnelCommand(["user", "login", "-g"], CaptureDiagnostics: false),
            cancellationToken).ConfigureAwait(false);
        if (loginResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Dev Tunnels GitHub sign-in failed. Run 'devtunnel user login -g' and complete the browser flow.");
        }

        var verifiedResult = await ExecuteCommandAsync(
            new DevTunnelCommand(["user", "show", "--json"]),
            cancellationToken).ConfigureAwait(false);
        if (verifiedResult.ExitCode != 0 || RequiresSignIn(verifiedResult))
        {
            throw new InvalidOperationException(
                "Dev Tunnels sign-in did not complete. Run 'devtunnel user login -g' and retry.");
        }
    }

    private async Task<DevTunnelProcessResult> ExecuteRequiredAsync(
        DevTunnelCommand command,
        string operation,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteCommandAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CommandFailure(operation, result);
        }

        return result;
    }

    /// <summary>
    /// Polls <c>devtunnel show &lt;id&gt; --json</c> until the forwarded callback port reports its
    /// public URI, the startup deadline elapses, the host process exits, or the caller cancels.
    /// </summary>
    private async Task<Uri> WaitForPublicPortUriAsync(int portNumber, CancellationToken cancellationToken)
    {
        var tunnelId = _tunnelId ?? throw new InvalidOperationException("The Dev Tunnel session has no tunnel.");

        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(_options.StartupTimeout);

        try
        {
            while (true)
            {
                // A host that has already exited can never publish the port; fail immediately
                // rather than polling until the deadline.
                ThrowIfHostExited();
                startupTimeout.Token.ThrowIfCancellationRequested();

                var show = await ExecuteCommandAsync(
                    new DevTunnelCommand(["show", tunnelId, "--json"]),
                    startupTimeout.Token).ConfigureAwait(false);

                if (show.ExitCode == 0)
                {
                    var lookup = DevTunnelShowParser.FindPortUri(show.StandardOutput, portNumber);
                    if (lookup.Status == DevTunnelPortUriStatus.Found)
                    {
                        return lookup.PortUri!;
                    }
                }

                await Task.Delay(_options.PortUriPollInterval, startupTimeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ThrowIfHostExited();
            throw new InvalidOperationException(
                "Dev Tunnels did not publish a public URL for the callback port before the startup timeout elapsed.");
        }
    }

    private async Task CleanupAfterFailedStartAsync()
    {
        _stopping = true;
        _hasStopped = true;
        try
        {
            await StopTunnelAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _callbackHost.StopAsync().ConfigureAwait(false);
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        _hasStopped = true;
        Exception? cleanupFailure = null;
        try
        {
            await StopTunnelAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        try
        {
            await _callbackHost.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure ??= exception;
        }

        if (cleanupFailure is not null)
        {
            if (cleanupFailure is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw cleanupFailure;
            }

            throw new InvalidOperationException(
                "The callback host stopped, but Dev Tunnel cleanup failed.",
                cleanupFailure);
        }
    }

    private async Task StopTunnelAsync(CancellationToken cancellationToken)
    {
        if (_hostProcess is not null)
        {
            using var shutdownTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            shutdownTimeout.CancelAfter(_options.ShutdownTimeout);
            await _hostProcess.TerminateAsync(shutdownTimeout.Token).ConfigureAwait(false);
            await _hostProcess.DisposeAsync().ConfigureAwait(false);
            _hostProcess = null;
        }

        if (_tunnelId is not null)
        {
            var tunnelId = _tunnelId;
            var result = await ExecuteCommandAsync(
                new DevTunnelCommand(["delete", tunnelId, "--force", "--json"]),
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw CommandFailure($"delete the session Dev Tunnel '{tunnelId}'", result);
            }

            _tunnelId = null;
        }
    }

    private async Task MonitorHostExitAsync(IDevTunnelHostProcess hostProcess)
    {
        var exitCode = await hostProcess.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        if (!_stopping)
        {
            throw new InvalidOperationException(
                $"The Dev Tunnels host process exited unexpectedly with code {exitCode}.");
        }
    }

    private void ThrowIfHostExited()
    {
        if (_unexpectedHostExit is { IsFaulted: true })
        {
            _unexpectedHostExit.GetAwaiter().GetResult();
        }

        if (_hostProcess?.HasExited == true)
        {
            throw new InvalidOperationException(
                "The Dev Tunnels host process exited unexpectedly.");
        }
    }

    /// <summary>
    /// Determines whether CLI output indicates absent or expired Dev Tunnels authentication.
    /// </summary>
    public static bool RequiresSignIn(DevTunnelProcessResult result)
    {
        var output = $"{result.StandardOutput}\n{result.StandardError}";
        return output.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
            || output.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
            || output.Contains("sign in", StringComparison.OrdinalIgnoreCase)
            || output.Contains("login", StringComparison.OrdinalIgnoreCase)
            || output.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || output.Contains("expired", StringComparison.OrdinalIgnoreCase);
    }

    private static InvalidOperationException CommandFailure(string operation, DevTunnelProcessResult result)
    {
        return new InvalidOperationException(
            $"Dev Tunnels could not {operation} (exit code {result.ExitCode}).");
    }

    private static string CreateTunnelId()
    {
        return $"sdcs-{Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant()}";
    }

    private static void ValidateOptions(DevTunnelSessionOptions options)
    {
        if (options.CommandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The command timeout must be positive.");
        }

        if (options.StartupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The startup timeout must be positive.");
        }

        if (options.PortUriPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The port URI poll interval must be positive.");
        }

        if (options.ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The shutdown timeout must be positive.");
        }
    }

    private async Task<DevTunnelProcessResult> ExecuteCommandAsync(
        DevTunnelCommand command,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.CommandTimeout);
        return await _processRunner.ExecuteAsync(command, timeout.Token).ConfigureAwait(false);
    }

    private static void ObserveTaskFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
