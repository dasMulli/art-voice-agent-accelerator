using ServiceDeskCallSimulator.Azure;
using ServiceDeskCallSimulator.UI;

namespace ServiceDeskCallSimulator.Tests;

/// <summary>
/// Verifies that the initial Azure authentication probe is deadline-bounded and that an expired
/// deadline drives the controller into an inline Error state that offers Retry. The probe
/// delegate is always a local stub, so no real credential is ever invoked.
/// </summary>
public sealed class AzureAuthenticationProbeTests
{
    [Fact]
    public void DefaultTimeout_IsInTheExpectedTwentyToThirtySecondBand()
    {
        Assert.InRange(
            AzureAuthenticationProbe.DefaultTimeout,
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void MainForm_UsesTheBoundedAuthenticationDeadline()
    {
        Assert.Equal(AzureAuthenticationProbe.DefaultTimeout, MainForm.AzureAuthProbeTimeout);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsWhenTheProbeCompletesInsideTheDeadline()
    {
        var observedToken = CancellationToken.None;

        await AzureAuthenticationProbe.ExecuteAsync(
            token =>
            {
                observedToken = token;
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.True(observedToken.CanBeCanceled, "The probe must receive a cancellable deadline token.");
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsTimeoutWhenACooperativeProbeStalls()
    {
        var exception = await Assert.ThrowsAsync<AzureAuthenticationTimeoutException>(
            () => AzureAuthenticationProbe.ExecuteAsync(
                token => Task.Delay(Timeout.Infinite, token),
                TimeSpan.FromMilliseconds(120),
                CancellationToken.None));

        Assert.Contains("Retry", exception.Message, StringComparison.Ordinal);
        Assert.Contains("az login", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsTimeoutEvenWhenTheProbeIgnoresCancellation()
    {
        using var neverCompletes = new SemaphoreSlim(0, 1);

        await Assert.ThrowsAsync<AzureAuthenticationTimeoutException>(
            () => AzureAuthenticationProbe.ExecuteAsync(
                _ => neverCompletes.WaitAsync(CancellationToken.None),
                TimeSpan.FromMilliseconds(120),
                CancellationToken.None));

        neverCompletes.Release();
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesFormLifetimeCancellationAsCancellationNotTimeout()
    {
        using var formLifetime = new CancellationTokenSource();

        var pending = AzureAuthenticationProbe.ExecuteAsync(
            token => Task.Delay(Timeout.Infinite, token),
            TimeSpan.FromSeconds(30),
            formLifetime.Token);

        await formLifetime.CancelAsync();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.IsNotType<AzureAuthenticationTimeoutException>(exception);
    }

    [Fact]
    public async Task ExecuteAsync_SurfacesTheUnderlyingAuthenticationFailureUnchanged()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureAuthenticationProbe.ExecuteAsync(
                _ => Task.FromException(new InvalidOperationException("boom")),
                TimeSpan.FromSeconds(5),
                CancellationToken.None));
    }

    [Fact]
    public void Controller_TurnsAnAuthenticationTimeoutIntoAnInlineErrorThatOffersRetry()
    {
        var controller = new SimulatorController();
        controller.BeginInitialization();
        controller.ReportStageStarted(InitializationStage.AzureAuthentication);

        Assert.Equal(
            InitializationStageStatus.InProgress,
            StatusOf(controller, InitializationStage.AzureAuthentication));

        var timeout = new AzureAuthenticationTimeoutException(TimeSpan.FromSeconds(25));
        controller.ReportStageFailed(InitializationStage.AzureAuthentication, timeout.Message);

        Assert.Equal(AppPhase.Error, controller.State.Phase);
        Assert.Equal(timeout.Message, controller.State.InitializationError);
        Assert.Equal(
            InitializationStageStatus.Failed,
            StatusOf(controller, InitializationStage.AzureAuthentication));
        Assert.True(SimulatorController.IsRetryEnabled(controller.State));
        Assert.False(SimulatorController.IsCallEnabled(controller.State));
    }

    private static InitializationStageStatus StatusOf(SimulatorController controller, InitializationStage stage) =>
        controller.State.Checklist.Single(item => item.Stage == stage).Status;
}
