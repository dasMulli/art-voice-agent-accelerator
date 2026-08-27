using ServiceDeskCallSimulator.Callback;
using ServiceDeskCallSimulator.Calls;
using ServiceDeskCallSimulator.DevTunnel;
using ServiceDeskCallSimulator.PhoneNumbers;
using ServiceDeskCallSimulator.Presets;
using ServiceDeskCallSimulator.UI;

namespace ServiceDeskCallSimulator.Tests;

/// <summary>
/// Exercises the production host-exit watcher and cleanup coordinator together while an
/// active-call lifecycle is deliberately held in finalization.
/// </summary>
public sealed class DevTunnelHostExitIntegrationTests
{
    [Fact]
    public async Task HostExitDuringActiveCall_FinalizesBeforeTunnelDisposalAndDefersRetry()
    {
        var callbackHost = new CallbackHost();
        var runner = new FakeProcessRunner
        {
            ShowJsonFactory = () => $$$"""
                {"tunnel":{"tunnelId":"sdcs-test","ports":[{"portNumber":{{{callbackHost.BoundPort}}},"protocol":"http","portUri":"https://sample.devtunnels.ms/"}]}}
                """,
        };
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        runner.Enqueue(new DevTunnelProcessResult(0, "{}", string.Empty));
        var session = new DevTunnelSession(
            callbackHost,
            runner,
            new DevTunnelSessionOptions
            {
                StartupTimeout = TimeSpan.FromSeconds(5),
                PortUriPollInterval = TimeSpan.FromMilliseconds(10),
            });
        var sessionDisposed = false;
        try
        {
            await session.StartAsync();

            var controller = ReadyController();
            Assert.True(controller.BeginDial());
            var callGate = new CallGenerationGate();
            var callGeneration = callGate.Advance();

            var coordinator = new TunnelFailureCleanupCoordinator();
            var watcher = new DevTunnelSessionWatcher();
            var order = new List<string>();
            var hangUpStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowCallFinalization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? cleanupTask = null;

            Func<bool> isCurrent = () => true;
            Func<Task<bool>> beginFailureAndRetire = () =>
            {
                controller.BeginTunnelFailureCleanup("The Dev Tunnel stopped unexpectedly.");
                callGate.Retire(callGeneration);
                order.Add("call-generation-retired");
                return Task.FromResult(true);
            };
            Func<Task> hangUpAndFinalize = async () =>
            {
                order.Add("hang-up-requested");
                hangUpStarted.TrySetResult();
                await allowCallFinalization.Task.ConfigureAwait(false);
                controller.CompleteCall("The Dev Tunnel stopped unexpectedly.");
                order.Add("call-finalized");
            };
            Func<Task> disposeFailedSession = async () =>
            {
                order.Add("tunnel-disposed");
                await session.DisposeAsync().ConfigureAwait(false);
                sessionDisposed = true;
            };
            Func<Task> completeFailureCleanup = () =>
            {
                controller.CompleteTunnelFailureCleanup();
                order.Add("retry-released");
                return Task.CompletedTask;
            };

            var observation = watcher.WatchAsync(
                session,
                isCurrent,
                () => cleanupTask = coordinator.HandleAsync(
                    isCurrent,
                    beginFailureAndRetire,
                    hangUpAndFinalize,
                    disposeFailedSession,
                    completeFailureCleanup),
                CancellationToken.None);

            runner.Host.Exit(57);
            await hangUpStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var duplicateCleanup = coordinator.HandleAsync(
                isCurrent,
                beginFailureAndRetire,
                hangUpAndFinalize,
                disposeFailedSession,
                completeFailureCleanup);

            Assert.NotNull(cleanupTask);
            Assert.Same(cleanupTask, duplicateCleanup);
            Assert.Equal(AppPhase.Error, controller.State.Phase);
            Assert.True(controller.State.IsTunnelFailureCleanupInProgress);
            Assert.False(callGate.IsCurrent(callGeneration));
            Assert.False(SimulatorController.IsRetryEnabled(controller.State));
            Assert.False(controller.BeginRetry());
            Assert.DoesNotContain("tunnel-disposed", order);

            allowCallFinalization.TrySetResult();
            await Task.WhenAll(observation, duplicateCleanup).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(
                ["call-generation-retired", "hang-up-requested", "call-finalized", "tunnel-disposed", "retry-released"],
                order);
            Assert.False(controller.State.IsTunnelFailureCleanupInProgress);
            Assert.True(SimulatorController.IsRetryEnabled(controller.State));
            Assert.True(controller.BeginRetry());
            Assert.False(session.HasRetainedTunnel);
        }
        finally
        {
            if (!sessionDisposed)
            {
                await session.DisposeAsync();
            }
        }
    }

    private static SimulatorController ReadyController()
    {
        var controller = new SimulatorController();
        controller.CompleteInitialization(
            new PhoneNumberSelectionResult(["+43800223359"], "+43800223359"),
            "+33801150311",
            ["[EN] Printer not working"],
            "sample.devtunnels.ms",
            "gpt-5.6-luna");
        controller.RequestPresetSelection(0, new CallerScriptDraft
        {
            Name = "[EN] Printer not working",
            Locale = "en-US",
            Voice = "en-US-JennyNeural",
            OpeningLine = "Hello.",
            Identity = "Maya",
            Background = "Background",
            Reason = "Reason",
            Urgency = "High",
            CallbackNumber = "+14155550101",
            AdditionalDetails = "Details",
        }, isDirty: false);
        return controller;
    }

    private sealed class FakeProcessRunner : IDevTunnelProcessRunner
    {
        private readonly Queue<DevTunnelProcessResult> _results = [];

        public FakeHostProcess Host { get; } = new();

        /// <summary>Supplies the stdout of every <c>show --json</c> command.</summary>
        public Func<string>? ShowJsonFactory { get; init; }

        public bool IsExecutableAvailable() => true;

        public Task<DevTunnelProcessResult> ExecuteAsync(
            DevTunnelCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                command.Arguments[0] == "show" && ShowJsonFactory is not null
                    ? new DevTunnelProcessResult(0, ShowJsonFactory(), string.Empty)
                    : _results.Dequeue());

        public Task<IDevTunnelHostProcess> StartHostAsync(
            DevTunnelCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult<IDevTunnelHostProcess>(Host);

        public void Enqueue(DevTunnelProcessResult result) => _results.Enqueue(result);
    }

    private sealed class FakeHostProcess : IDevTunnelHostProcess
    {
        private readonly TaskCompletionSource<int> _exitCode =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasExited => _exitCode.Task.IsCompleted;

        public string Diagnostics => string.Empty;

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
            _exitCode.Task.WaitAsync(cancellationToken);

        public Task<string> WaitForOutputAsync(
            Func<string, bool> predicate,
            CancellationToken cancellationToken) =>
            Task.FromResult("https://sample.devtunnels.ms");

        public Task TerminateAsync(CancellationToken cancellationToken)
        {
            Exit(0);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Exit(int exitCode) => _exitCode.TrySetResult(exitCode);
    }
}
