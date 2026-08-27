using ServiceDeskCallSimulator.Callback;
using ServiceDeskCallSimulator.DevTunnel;
using ServiceDeskCallSimulator.UI;

namespace ServiceDeskCallSimulator.Tests;

public sealed class DevTunnelSessionTests
{
    private static readonly DevTunnelSessionOptions FastPolling = new()
    {
        StartupTimeout = TimeSpan.FromSeconds(5),
        PortUriPollInterval = TimeSpan.FromMilliseconds(10),
    };

    private const string ShowJsonWithNoPorts = """{"tunnel":{"tunnelId":"sdcs-test","ports":[]}}""";

    private static string ShowJson(int portNumber, string portUri = "https://sample-1234.euw.devtunnels.ms/") =>
        $$$"""
        {"tunnel":{"tunnelId":"sdcs-test","clusterId":"euw","ports":[{"portNumber":{{{portNumber}}},"protocol":"http","portUri":"{{{portUri}}}"}]}}
        """;

    private static string ShowJsonWithoutPortUri(int portNumber) =>
        $$$"""
        {"tunnel":{"tunnelId":"sdcs-test","clusterId":"euw","ports":[{"portNumber":{{{portNumber}}},"protocol":"http"}]}}
        """;

    [Fact]
    public async Task Session_UsesHttpAnonymousCommandsAndDeletesOnlyItsTunnel()
    {
        var callbackHost = new CallbackHost();
        var runner = new FakeProcessRunner();
        runner.Enqueue(new DevTunnelProcessResult(0, """{"user":{"name":"test"}}""", string.Empty));
        runner.Enqueue(new DevTunnelProcessResult(0, """{"tunnelId":"created"}""", string.Empty));
        runner.Enqueue(new DevTunnelProcessResult(0, """{"portNumber":1}""", string.Empty));
        runner.ShowJsonFactory = () => ShowJson(callbackHost.BoundPort);
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        await using var session = new DevTunnelSession(callbackHost, runner, FastPolling);

        await session.StartAsync();
        var createdTunnelId = runner.Commands[1].Arguments[1];

        Assert.Equal("https", session.PublicEventUri.Scheme);
        Assert.Equal("wss", session.PublicMediaUri.Scheme);
        Assert.Equal("sample-1234.euw.devtunnels.ms", session.PublicEventUri.Host);
        Assert.Contains("--protocol", runner.Commands[2].Arguments);
        Assert.Contains("http", runner.Commands[2].Arguments);
        Assert.Contains("--allow-anonymous", runner.Commands[1].Arguments);
        Assert.DoesNotContain(
            runner.Commands.SelectMany(command => command.Arguments),
            argument => argument.Contains("token", StringComparison.OrdinalIgnoreCase));

        await session.StopAsync();

        Assert.True(runner.Host.Terminated);
        Assert.Equal(["delete", createdTunnelId, "--force", "--json"], runner.Commands[^1].Arguments);
    }

    /// <summary>
    /// Live regression: <c>devtunnel host ID --port-number N --protocol http --allow-anonymous</c>
    /// exits 1 with "Invalid arguments. Batch update of ports is not supported. Add, update, or
    /// delete ports individually instead." The host command must carry the tunnel ID only; the port
    /// and anonymous access were already configured by <c>create</c> and <c>port create</c>.
    /// </summary>
    [Fact]
    public async Task HostCommand_CarriesTheTunnelIdOnlyAndNoPortOrAccessMutationFlags()
    {
        var callbackHost = new CallbackHost();
        var runner = StartedRunner(callbackHost);
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        await using var session = new DevTunnelSession(callbackHost, runner, FastPolling);

        await session.StartAsync();

        var host = runner.HostCommand!;
        Assert.Equal(["host", session.TunnelId], host.Arguments);
        Assert.DoesNotContain("--port-number", host.Arguments);
        Assert.DoesNotContain("--protocol", host.Arguments);
        Assert.DoesNotContain("--allow-anonymous", host.Arguments);
        Assert.DoesNotContain("--json", host.Arguments);
        Assert.DoesNotContain(host.Arguments, argument => argument.StartsWith('-'));
    }

    [Fact]
    public async Task Startup_PollsStructuredShowJsonUntilTheMatchingPortPublishesItsUri()
    {
        var callbackHost = new CallbackHost();
        var runner = StartedRunner(callbackHost);
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));

        var showCalls = 0;
        runner.ShowJsonFactory = () => ++showCalls switch
        {
            1 => ShowJsonWithNoPorts,                             // port not listed yet
            2 => ShowJsonWithoutPortUri(callbackHost.BoundPort),  // listed, no public URI yet
            _ => ShowJson(callbackHost.BoundPort),
        };
        await using var session = new DevTunnelSession(callbackHost, runner, FastPolling);

        await session.StartAsync();

        Assert.Equal(3, showCalls);
        var showCommands = runner.Commands.Where(command => command.Arguments[0] == "show").ToArray();
        Assert.Equal(3, showCommands.Length);
        Assert.All(
            showCommands,
            command => Assert.Equal(["show", session.TunnelId, "--json"], command.Arguments));
        Assert.Equal("sample-1234.euw.devtunnels.ms", session.PublicEventUri.Host);

        // The host is started exactly once, before polling begins.
        Assert.Single(runner.HostStarts);
    }

    [Fact]
    public async Task Startup_SelectsOnlyTheCallbackPortWhenTheTunnelForwardsSeveralPorts()
    {
        var callbackHost = new CallbackHost();
        var runner = StartedRunner(callbackHost);
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        runner.ShowJsonFactory = () => $$$"""
            {"tunnel":{"tunnelId":"sdcs-test","tunnelUri":"https://sample.euw.devtunnels.ms/","ports":[
              {"portNumber":{{{callbackHost.BoundPort + 1}}},"protocol":"http","portUri":"https://other-9999.euw.devtunnels.ms/"},
              {"portNumber":{{{callbackHost.BoundPort}}},"protocol":"http","portUri":"https://sample-1234.euw.devtunnels.ms/"}
            ]}}
            """;
        await using var session = new DevTunnelSession(callbackHost, runner, FastPolling);

        await session.StartAsync();

        Assert.Equal("sample-1234.euw.devtunnels.ms", session.PublicEventUri.Host);
        Assert.Equal("sample-1234.euw.devtunnels.ms", session.PublicMediaUri.Host);
    }

    [Fact]
    public async Task Startup_FailsAndCleansUpWhenTheStartupTimeoutElapsesWithoutAPortUri()
    {
        var callbackHost = new CallbackHost();
        var runner = StartedRunner(callbackHost);
        runner.ShowJsonFactory = () => ShowJsonWithoutPortUri(callbackHost.BoundPort);
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        await using var session = new DevTunnelSession(
            callbackHost,
            runner,
            new DevTunnelSessionOptions
            {
                StartupTimeout = TimeSpan.FromMilliseconds(150),
                PortUriPollInterval = TimeSpan.FromMilliseconds(10),
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());

        Assert.Contains("startup timeout", exception.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(runner.Host.Terminated);
        Assert.Equal("delete", runner.Commands[^1].Arguments[0]);
        Assert.False(session.HasRetainedTunnel);
    }

    [Fact]
    public async Task Startup_FailsImmediatelyWhenTheHostProcessExitsWhilePolling()
    {
        var callbackHost = new CallbackHost();
        var runner = StartedRunner(callbackHost);
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        runner.ShowJsonFactory = () =>
        {
            runner.Host.Exit(7);
            return ShowJsonWithNoPorts;
        };
        await using var session = new DevTunnelSession(callbackHost, runner, FastPolling);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());

        Assert.Contains("exited unexpectedly", exception.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("delete", runner.Commands[^1].Arguments[0]);
    }

    [Fact]
    public async Task Startup_FailsWhenShowReportsAnUnusablePortUri()
    {
        var callbackHost = new CallbackHost();
        var runner = StartedRunner(callbackHost);
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        runner.ShowJsonFactory = () => ShowJson(callbackHost.BoundPort, "http://evil.example.com/");
        await using var session = new DevTunnelSession(callbackHost, runner, FastPolling);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());

        Assert.Contains("unusable public URL", exception.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("delete", runner.Commands[^1].Arguments[0]);
    }

    [Fact]
    public async Task Startup_SurfacesCancellationWhilePollingWithoutClaimingATimeout()
    {
        var callbackHost = new CallbackHost();
        var runner = StartedRunner(callbackHost);
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        using var cancellation = new CancellationTokenSource();
        var pollStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.ShowJsonFactory = () =>
        {
            pollStarted.TrySetResult();
            return ShowJsonWithNoPorts;
        };
        await using var session = new DevTunnelSession(callbackHost, runner, FastPolling);

        var start = session.StartAsync(cancellation.Token);
        await pollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => start);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public async Task PartialPortFailure_DeletesOnlyCreatedSessionTunnel()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        runner.Enqueue(new DevTunnelProcessResult(1, string.Empty, "port failed"));
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        await using var session = new DevTunnelSession(new CallbackHost(), runner, FastPolling);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
        var createdTunnelId = runner.Commands[1].Arguments[1];

        Assert.Contains("Unable to start", exception.Message);
        Assert.Equal(["delete", createdTunnelId, "--force", "--json"], runner.Commands[^1].Arguments);
        Assert.Null(runner.HostCommand);
    }

    [Fact]
    public async Task FailedCreate_DoesNotOwnOrDeleteItsCandidateTunnel()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        runner.Enqueue(new DevTunnelProcessResult(1, string.Empty, "create failed"));
        await using var session = new DevTunnelSession(new CallbackHost(), runner, FastPolling);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());

        Assert.Equal(2, runner.Commands.Count);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments[0] == "delete");
    }

    [Fact]
    public async Task FailedDelete_IsRetainedAndRetriedDuringDisposal()
    {
        var callbackHost = new CallbackHost();
        var runner = StartedRunner(callbackHost);
        runner.Enqueue(new DevTunnelProcessResult(1, string.Empty, "delete failed"));
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        var session = new DevTunnelSession(callbackHost, runner, FastPolling);
        await session.StartAsync();
        var tunnelId = session.TunnelId;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StopAsync());
        Assert.Equal(tunnelId, session.TunnelId);
        Assert.True(session.HasRetainedTunnel);

        await session.DisposeAsync();

        Assert.False(session.HasRetainedTunnel);
        Assert.Equal(
            2,
            runner.Commands.Count(command =>
                command.Arguments is ["delete", var deletedTunnelId, "--force", "--json"]
                && deletedTunnelId == tunnelId));
    }

    [Fact]
    public async Task CancelledDelete_IsRetainedAndCanBeRetried()
    {
        var callbackHost = new CallbackHost();
        var runner = StartedRunner(callbackHost);
        var deleteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteAttempts = 0;
        runner.ExecuteOverride = async (command, cancellationToken) =>
        {
            if (command.Arguments[0] != "delete")
            {
                return runner.DefaultResult(command);
            }

            if (Interlocked.Increment(ref deleteAttempts) == 1)
            {
                deleteStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new DevTunnelProcessResult(0, "{}", string.Empty);
        };
        await using var session = new DevTunnelSession(callbackHost, runner, FastPolling);
        await session.StartAsync();
        var tunnelId = session.TunnelId;
        using var cancellation = new CancellationTokenSource();

        var stopping = session.StopAsync(cancellation.Token);
        await deleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping);
        Assert.Equal(tunnelId, session.TunnelId);

        await session.StopAsync();
        Assert.Equal(2, deleteAttempts);
    }

    [Theory]
    [InlineData(1, "Not logged in. Run devtunnel user login.", "", true)]
    [InlineData(1, "", "Authentication token expired.", true)]
    [InlineData(1, "", "network unavailable", false)]
    [InlineData(0, "{\"user\":\"test\"}", "", false)]
    public void RequiresSignIn_DetectsOnlyAuthenticationFailures(
        int exitCode,
        string standardOutput,
        string standardError,
        bool expected)
    {
        Assert.Equal(
            expected,
            DevTunnelSession.RequiresSignIn(new DevTunnelProcessResult(exitCode, standardOutput, standardError)));
    }

    [Fact]
    public async Task Session_UsesGitHubLoginOnlyWhenAuthenticationIsRequired()
    {
        var callbackHost = new CallbackHost();
        var runner = new FakeProcessRunner();
        runner.Enqueue(new DevTunnelProcessResult(1, string.Empty, "Not logged in."));
        runner.Enqueue(new DevTunnelProcessResult(0, string.Empty, string.Empty));
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        runner.ShowJsonFactory = () => ShowJson(callbackHost.BoundPort);
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        await using var session = new DevTunnelSession(callbackHost, runner, FastPolling);

        await session.StartAsync();

        Assert.Equal(["user", "login", "-g"], runner.Commands[1].Arguments);
        Assert.False(runner.Commands[1].CaptureDiagnostics);
    }

    [Fact]
    public void UrlParser_RejectsMalformedAndAmbiguousOutput()
    {
        var jsonUrl = DevTunnelUrlParser.ParseHttpsForwardingUrlFromJson(
            """{"tunnelEndpoints":["https://sample.devtunnels.ms:12345"]}""");
        var hostUrl = DevTunnelUrlParser.ParseHttpsForwardingUrlFromHostOutput(
            "Hosting at https://sample.devtunnels.ms:12345/");

        Assert.Equal("https://sample.devtunnels.ms:12345/", jsonUrl!.AbsoluteUri);
        Assert.Equal(jsonUrl, hostUrl);
        Assert.Throws<InvalidOperationException>(() =>
            DevTunnelUrlParser.ParseHttpsForwardingUrlFromJson("{not json"));
        Assert.Throws<InvalidOperationException>(() =>
            DevTunnelUrlParser.ParseHttpsForwardingUrlFromHostOutput(
                "https://one.devtunnels.ms https://two.devtunnels.ms"));
    }

    [Fact]
    public async Task SessionWatcher_ObservesTheExactCurrentSessionHostFault()
    {
        var callbackHost = new CallbackHost();
        var runner = StartedRunner(callbackHost);
        var session = new DevTunnelSession(callbackHost, runner, FastPolling);
        await session.StartAsync();
        var watcher = new DevTunnelSessionWatcher();
        var handled = 0;

        var observation = watcher.WatchAsync(
            session,
            () => true,
            () =>
            {
                handled++;
                return Task.CompletedTask;
            },
            CancellationToken.None);
        runner.Host.Exit(31);
        await observation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, handled);
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task SessionWatcher_IgnoresAStaleSessionAfterReplacement()
    {
        var callbackHost = new CallbackHost();
        var runner = StartedRunner(callbackHost);
        var session = new DevTunnelSession(callbackHost, runner, FastPolling);
        await session.StartAsync();
        var watcher = new DevTunnelSessionWatcher();
        var handled = 0;

        var observation = watcher.WatchAsync(
            session,
            () => false,
            () =>
            {
                handled++;
                return Task.CompletedTask;
            },
            CancellationToken.None);
        runner.Host.Exit(32);
        await observation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, handled);
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        await session.DisposeAsync();
    }

    private sealed class FakeProcessRunner : IDevTunnelProcessRunner
    {
        private readonly Queue<DevTunnelProcessResult> _results = new();

        public List<DevTunnelCommand> Commands { get; } = [];

        public FakeHostProcess Host { get; } = new();

        public DevTunnelCommand? HostCommand { get; private set; }

        public List<DevTunnelCommand> HostStarts { get; } = [];

        /// <summary>Supplies the stdout of every <c>show --json</c> command.</summary>
        public Func<string>? ShowJsonFactory { get; set; }

        public Func<DevTunnelCommand, CancellationToken, Task<DevTunnelProcessResult>>? ExecuteOverride { get; set; }

        public bool IsExecutableAvailable() => true;

        public Task<DevTunnelProcessResult> ExecuteAsync(
            DevTunnelCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return ExecuteOverride is not null
                ? ExecuteOverride(command, cancellationToken)
                : Task.FromResult(DefaultResult(command));
        }

        public Task<IDevTunnelHostProcess> StartHostAsync(
            DevTunnelCommand command,
            CancellationToken cancellationToken)
        {
            HostCommand = command;
            HostStarts.Add(command);
            return Task.FromResult<IDevTunnelHostProcess>(Host);
        }

        public void Enqueue(DevTunnelProcessResult result) => _results.Enqueue(result);

        public DevTunnelProcessResult DefaultResult(DevTunnelCommand command)
        {
            if (command.Arguments[0] == "show" && ShowJsonFactory is not null)
            {
                return new DevTunnelProcessResult(0, ShowJsonFactory(), string.Empty);
            }

            return _results.Dequeue();
        }
    }

    private sealed class FakeHostProcess : IDevTunnelHostProcess
    {
        private readonly TaskCompletionSource<int> _exitCode = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _output = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasExited => _exitCode.Task.IsCompleted;

        public string Diagnostics => "host started";

        public bool Terminated { get; private set; }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
            _exitCode.Task.WaitAsync(cancellationToken);

        public async Task<string> WaitForOutputAsync(Func<string, bool> predicate, CancellationToken cancellationToken)
        {
            var output = await _output.Task.WaitAsync(cancellationToken);
            if (!predicate(output))
            {
                throw new InvalidOperationException("The fake host output did not match the requested predicate.");
            }

            return output;
        }

        public Task TerminateAsync(CancellationToken cancellationToken)
        {
            Terminated = true;
            Exit(0);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Exit(int exitCode) => _exitCode.TrySetResult(exitCode);
    }

    /// <summary>
    /// A runner primed through sign-in, create, and port create, whose <c>show --json</c> reports
    /// the callback port's public URI immediately.
    /// </summary>
    private static FakeProcessRunner StartedRunner(CallbackHost callbackHost)
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        runner.ShowJsonFactory = () => ShowJson(callbackHost.BoundPort);
        return runner;
    }
}
