using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using ServiceDeskCallSimulator.Azure;
using ServiceDeskCallSimulator.Configuration;

namespace ServiceDeskCallSimulator.Tests;

/// <summary>
/// Verifies the deterministic local developer credential chain and its single-instance sharing
/// across every Azure client. No test here ever requests a token, so no Azure CLI, Visual Studio,
/// PowerShell, or network access occurs.
/// </summary>
public sealed class AzureCredentialFactoryTests
{
    [Fact]
    public void CreateLocalDeveloperCredentialSources_OrdersAzureCliFirstThenVisualStudioThenPowerShell()
    {
        var sources = AzureCredentialFactory.CreateLocalDeveloperCredentialSources();

        Assert.Equal(3, sources.Count);
        Assert.IsType<AzureCliCredential>(sources[0]);
        Assert.IsType<VisualStudioCredential>(sources[1]);
        Assert.IsType<AzurePowerShellCredential>(sources[2]);
    }

    [Fact]
    public void CreateLocalDeveloperCredentialSources_ContainsNoBroadOrInteractiveCredential()
    {
        var sources = AzureCredentialFactory.CreateLocalDeveloperCredentialSources();

        Assert.DoesNotContain(sources, source => source is DefaultAzureCredential);
        Assert.DoesNotContain(sources, source => source is InteractiveBrowserCredential);
        Assert.DoesNotContain(sources, source => source is EnvironmentCredential);
        Assert.DoesNotContain(sources, source => source is ManagedIdentityCredential);
    }

    [Fact]
    public void CreateLocalDeveloperCredential_IsADeterministicChainNotDefaultAzureCredential()
    {
        var credential = AzureCredentialFactory.CreateLocalDeveloperCredential();

        Assert.IsType<ChainedTokenCredential>(credential);
        Assert.IsNotType<DefaultAzureCredential>(credential);
    }

    [Fact]
    public void DeveloperToolProcessTimeout_IsBoundedSoAStalledCliCannotBlockStartup()
    {
        Assert.True(AzureCredentialFactory.DeveloperToolProcessTimeout > TimeSpan.Zero);
        Assert.True(
            AzureCredentialFactory.DeveloperToolProcessTimeout <= TimeSpan.FromSeconds(30),
            "The Azure CLI process timeout must stay well inside the authentication deadline.");
        Assert.True(
            AzureCredentialFactory.DeveloperToolProcessTimeout < AzureAuthenticationProbe.DefaultTimeout,
            "Each credential process must be able to fail before the overall probe deadline elapses.");
    }

    [Fact]
    public void Composition_SharesTheExactSameTokenCredentialInstanceWithEveryAzureClient()
    {
        var settings = new SimulatorSettings();
        using var services = new ServiceCollection()
            .AddServiceDeskCallSimulatorCore(settings)
            .BuildServiceProvider();

        var credential = services.GetRequiredService<TokenCredential>();

        Assert.IsType<ChainedTokenCredential>(credential);
        Assert.Same(credential, services.GetRequiredService<TokenCredential>());

        // Resolving all credential-consuming clients must not create a second chain.
        Assert.NotNull(services.GetRequiredService<global::Azure.AI.OpenAI.AzureOpenAIClient>());
        Assert.NotNull(services.GetRequiredService<global::Azure.Communication.CallAutomation.CallAutomationClient>());
        Assert.NotNull(services.GetRequiredService<global::Azure.Communication.PhoneNumbers.PhoneNumbersClient>());
        Assert.Same(credential, services.GetRequiredService<TokenCredential>());
    }

    [Fact]
    public void Composition_DoesNotRegisterABroadDefaultAzureCredential()
    {
        var settings = new SimulatorSettings();
        using var services = new ServiceCollection()
            .AddServiceDeskCallSimulatorCore(settings)
            .BuildServiceProvider();

        Assert.Null(services.GetService<DefaultAzureCredential>());
        Assert.Null(services.GetService<ChainedTokenCredential>());
    }
}
