using Azure.Communication.PhoneNumbers;
using Azure.Communication.CallAutomation;
using Azure.AI.OpenAI;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using ServiceDeskCallSimulator.Configuration;
using ServiceDeskCallSimulator.Conversation;
using ServiceDeskCallSimulator.Calls;
using ServiceDeskCallSimulator.Monitoring;
using ServiceDeskCallSimulator.PhoneNumbers;
using ServiceDeskCallSimulator.Speech;

namespace ServiceDeskCallSimulator.Azure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddServiceDeskCallSimulatorCore(
        this IServiceCollection services,
        SimulatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);

        services.AddSingleton(settings);

        // One shared, deterministic local developer credential instance for every Azure client:
        // Azure CLI first, then Visual Studio, then Azure PowerShell. Registering only
        // TokenCredential keeps a single token cache and prevents any second (slower) chain from
        // being resolved by accident.
        services.AddSingleton<TokenCredential>(_ => AzureCredentialFactory.CreateLocalDeveloperCredential());
        services.AddSingleton(sp => new AzureOpenAIClient(
            new Uri(settings.AiServices.Endpoint),
            sp.GetRequiredService<TokenCredential>()));
        services.AddSingleton<IGroundedReplyGenerator>(sp => new AzureGroundedReplyGenerator(
            sp.GetRequiredService<AzureOpenAIClient>(),
            settings.AiServices.TextDeployment));
        services.AddSingleton<ISpeechPipelineFactory>(sp => new AzureSpeechPipelineFactory(
            new Uri(settings.AiServices.Endpoint),
            sp.GetRequiredService<TokenCredential>()));
        services.AddSingleton<IAudioMonitorFactory>(_ => new WaveOutAudioMonitorFactory());
        services.AddSingleton(sp => new CallAutomationClient(
            new Uri(settings.Acs.Endpoint),
            sp.GetRequiredService<TokenCredential>()));
        services.AddSingleton<ICallAutomationGateway>(sp => new AcsCallAutomationGateway(
            sp.GetRequiredService<CallAutomationClient>()));
        services.AddSingleton(sp => new PhoneNumbersClient(new Uri(settings.Acs.Endpoint), sp.GetRequiredService<TokenCredential>()));
        services.AddSingleton(sp => new AcsPhoneNumberDiscovery(sp.GetRequiredService<PhoneNumbersClient>(), settings.Acs.PreferredCallerId));

        return services;
    }
}
