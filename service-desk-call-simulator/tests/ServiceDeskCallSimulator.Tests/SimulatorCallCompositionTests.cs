using Azure.AI.OpenAI;
using Azure.Communication.CallAutomation;
using Azure.Communication.PhoneNumbers;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Channels;
using ServiceDeskCallSimulator.Azure;
using ServiceDeskCallSimulator.Calls;
using ServiceDeskCallSimulator.Configuration;
using ServiceDeskCallSimulator.Conversation;
using ServiceDeskCallSimulator.Media;
using ServiceDeskCallSimulator.Monitoring;
using ServiceDeskCallSimulator.PhoneNumbers;
using ServiceDeskCallSimulator.Presets;
using ServiceDeskCallSimulator.Speech;
using ServiceDeskCallSimulator.UI;

namespace ServiceDeskCallSimulator.Tests;

public sealed class SimulatorCallCompositionTests
{
    [Fact]
    public void AppsettingsComposition_ResolvesSingletonFactoriesAndClientsWithoutNetworkCalls()
    {
        var settings = SimulatorConfiguration.LoadSettingsFrom(
            GetProjectSourceDirectory(),
            environmentVariablePrefix: null);
        Assert.Equal("+43800223359", settings.Acs.PreferredCallerId);

        var services = new ServiceCollection()
            .AddServiceDeskCallSimulatorCore(settings)
            .BuildServiceProvider();

        using (services)
        {
            // One deterministic local developer credential instance is shared by every Azure
            // client; no broad DefaultAzureCredential is registered any more.
            var credential = services.GetRequiredService<TokenCredential>();
            Assert.IsType<ChainedTokenCredential>(credential);
            Assert.Same(credential, services.GetRequiredService<TokenCredential>());
            Assert.Null(services.GetService<DefaultAzureCredential>());
            Assert.IsType<AzureOpenAIClient>(services.GetRequiredService<AzureOpenAIClient>());
            Assert.IsType<AzureGroundedReplyGenerator>(services.GetRequiredService<IGroundedReplyGenerator>());
            Assert.IsType<AzureSpeechPipelineFactory>(services.GetRequiredService<ISpeechPipelineFactory>());
            Assert.IsType<WaveOutAudioMonitorFactory>(services.GetRequiredService<IAudioMonitorFactory>());
            Assert.IsType<CallAutomationClient>(services.GetRequiredService<CallAutomationClient>());
            Assert.IsType<AcsCallAutomationGateway>(services.GetRequiredService<ICallAutomationGateway>());
            Assert.IsType<PhoneNumbersClient>(services.GetRequiredService<PhoneNumbersClient>());
            Assert.IsType<AcsPhoneNumberDiscovery>(services.GetRequiredService<AcsPhoneNumberDiscovery>());

            Assert.Same(
                services.GetRequiredService<IGroundedReplyGenerator>(),
                services.GetRequiredService<IGroundedReplyGenerator>());
            Assert.Same(
                services.GetRequiredService<ISpeechPipelineFactory>(),
                services.GetRequiredService<ISpeechPipelineFactory>());
            Assert.Same(
                services.GetRequiredService<IAudioMonitorFactory>(),
                services.GetRequiredService<IAudioMonitorFactory>());
            Assert.Same(
                services.GetRequiredService<ICallAutomationGateway>(),
                services.GetRequiredService<ICallAutomationGateway>());
            Assert.Same(
                services.GetRequiredService<AcsPhoneNumberDiscovery>(),
                services.GetRequiredService<AcsPhoneNumberDiscovery>());
        }
    }

    [Fact]
    public async Task CreateAsync_ComposesSnapshotMutedMonitorAndUiOrderedDisposal()
    {
        var draft = CreateDraft();
        var order = new List<string>();
        var call = new TrackingOwnedCallSession(order);
        var speech = new TrackingSpeechPipeline(order);
        var monitor = new TrackingAudioMonitor(order);

        var resources = await SimulatorCallComposition.CreateAsync(
            draft,
            mutedLocally: true,
            () => call,
            new DelegatingSpeechPipelineFactory(() => speech),
            new DelegatingAudioMonitorFactory(() => monitor),
            new NoOpReplyGenerator());
        draft.Identity = "Changed after composition";

        try
        {
            Assert.Equal("Maya", resources.Script.Identity);
            Assert.True(resources.Monitor.IsMuted);
            Assert.True(monitor.IsMuted);
            Assert.Same(call, resources.CallSession);
            Assert.Same(resources.CallSession.CallerMediaTransport, call.CallerMediaTransport);
        }
        finally
        {
            await SimulatorCallComposition.DisposeAsync(resources);
        }

        Assert.Equal(
            ["speech-stop", "monitor-stop", "speech-dispose", "monitor-dispose", "call-dispose"],
            order);
    }

    [Fact]
    public async Task CreateAsync_WhenMonitorConstructionFails_UsesNullMonitorReportsSafeDiagnosticAndRetainsCallResources()
    {
        var draft = CreateDraft();
        var order = new List<string>();
        var call = new TrackingOwnedCallSession(order);
        var speech = new TrackingSpeechPipeline(order);
        var reportedFaults = new List<AudioMonitorFault>();

        var resources = await SimulatorCallComposition.CreateAsync(
            draft,
            mutedLocally: true,
            () => call,
            new DelegatingSpeechPipelineFactory(() => speech),
            new DelegatingAudioMonitorFactory(
                () => throw new InvalidOperationException("Device path C:\\sensitive-output should remain private.")),
            new NoOpReplyGenerator(),
            reportedFaults.Add);

        try
        {
            Assert.IsType<NullAudioMonitor>(resources.Monitor);
            Assert.True(resources.Monitor.IsMuted);
            Assert.Same(call, resources.CallSession);
            Assert.Same(resources.CallSession.CallerMediaTransport, call.CallerMediaTransport);
            Assert.Empty(order);

            var fault = Assert.Single(reportedFaults);
            Assert.Equal("startup", fault.Operation);
            Assert.Equal(
                "Local audio playback could not be started, so listen-along was disabled. The call continues.",
                fault.Message);
            Assert.DoesNotContain("sensitive-output", fault.Message, StringComparison.Ordinal);
        }
        finally
        {
            await SimulatorCallComposition.DisposeAsync(resources);
        }

        Assert.Equal(["speech-stop", "speech-dispose", "call-dispose"], order);
    }

    private static string GetProjectSourceDirectory()
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "ServiceDeskCallSimulator"));
    }

    private static CallerScriptDraft CreateDraft() => new()
    {
        Name = "[EN] Printer not working",
        Locale = "en-US",
        Voice = "en-US-JennyNeural",
        OpeningLine = "Hello, this is Maya.",
        Identity = "Maya",
        Background = "The office printer is offline.",
        Reason = "Need a status check.",
        Urgency = "High",
        CallbackNumber = "+14155550101",
        AdditionalDetails = "Please call back once the queue is clear.",
    };

    private sealed class TrackingOwnedCallSession(List<string> order) : IOwnedCallerCallSession
    {
        public Task ConnectionReady => Task.CompletedTask;

        public CallSessionState State => CallSessionState.Ended;

        public ICallMediaTransport CallerMediaTransport { get; } = new NoOpMediaTransport();

        public event EventHandler<CallStateChange>? StateChanged
        {
            add { }
            remove { }
        }

        public Task HangUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            order.Add("call-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingSpeechPipeline(List<string> order) : ISpeechPipeline
    {
        public event EventHandler<SpeechRecognitionUpdate>? RecognitionUpdated
        {
            add { }
            remove { }
        }

        public Task StartRecognitionAsync(string locale, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask WritePcmAsync(ReadOnlyMemory<byte> pcm16KMono, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public Task<byte[]> SynthesizeAsync(string voice, string text, CancellationToken cancellationToken) =>
            Task.FromResult<byte[]>([1, 2]);

        public Task StopRecognitionAsync()
        {
            order.Add("speech-stop");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            order.Add("speech-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingAudioMonitor(List<string> order) : IAudioMonitor
    {
        public bool IsMuted { get; set; }

        public event EventHandler<AudioMonitorFault>? Faulted
        {
            add { }
            remove { }
        }

        public bool TryMonitor(ReadOnlyMemory<byte> pcm16KMono) => true;

        public Task StopAsync()
        {
            order.Add("monitor-stop");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            order.Add("monitor-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DelegatingSpeechPipelineFactory(Func<ISpeechPipeline> create) : ISpeechPipelineFactory
    {
        public ISpeechPipeline Create() => create();
    }

    private sealed class DelegatingAudioMonitorFactory(Func<IAudioMonitor> create) : IAudioMonitorFactory
    {
        public IAudioMonitor Create() => create();
    }

    private sealed class NoOpReplyGenerator : IGroundedReplyGenerator
    {
        public Task<GroundedModelDecision> GenerateAsync(
            CallerScriptSnapshot script,
            IReadOnlyList<TranscriptTurn> transcript,
            CancellationToken cancellationToken) =>
            Task.FromResult(new GroundedModelDecision(GroundedReplyAction.Reply, null, "unused"));
    }

    private sealed class NoOpMediaTransport : ICallMediaTransport
    {
        private readonly TaskCompletionSource _disconnected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ConnectionReady => Task.CompletedTask;

        public Task Disconnected => _disconnected.Task;

        public ChannelReader<AcsInboundAudioFrame> InboundFrames =>
            Channel.CreateUnbounded<AcsInboundAudioFrame>().Reader;

        public long CreateAudioGeneration() => 1;

        public Task SendAudioAsync(
            long generation,
            ReadOnlyMemory<byte> pcm16KMono,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAudioAsync(long generation, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
