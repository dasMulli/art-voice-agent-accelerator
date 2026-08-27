using System.Collections.Concurrent;
using System.Threading.Channels;
using ServiceDeskCallSimulator.Calls;
using ServiceDeskCallSimulator.Conversation;
using ServiceDeskCallSimulator.Media;
using ServiceDeskCallSimulator.Monitoring;
using ServiceDeskCallSimulator.Presets;
using ServiceDeskCallSimulator.Speech;

namespace ServiceDeskCallSimulator.Tests;

public sealed class ScriptedCallerOrchestratorTests
{
    [Fact]
    public void SnapshotAndPrompt_ContainEveryFrozenFactHistoryLanguageGroundingAndSchema()
    {
        var draft = CreateDraft();
        var script = CallerScriptSnapshot.FromDraft(draft);
        draft.Identity = "Changed after call start";
        var history = new[]
        {
            new TranscriptTurn(
                DateTimeOffset.Parse("2026-08-26T18:00:00Z"),
                TranscriptSpeaker.ServiceDesk,
                "Which device has the problem?",
                TranscriptStatus.Final),
        };

        var prompt = GroundedPromptBuilder.BuildDeveloperPrompt(script);
        var conversation = GroundedPromptBuilder.BuildConversationMessage(history);

        Assert.Equal("Maya", script.Identity);
        foreach (var field in new[]
        {
            script.Name,
            script.Locale,
            script.Voice,
            script.OpeningLine,
            script.Identity,
            script.Background,
            script.Reason,
            script.Urgency,
            script.CallbackNumber,
            script.AdditionalDetails,
        })
        {
            Assert.Contains(field, prompt, StringComparison.Ordinal);
        }

        Assert.Contains("German", prompt, StringComparison.Ordinal);
        Assert.Contains("Never invent", prompt, StringComparison.Ordinal);
        Assert.Contains("hang_up", prompt, StringComparison.Ordinal);
        Assert.Contains("Which device has the problem?", conversation, StringComparison.Ordinal);
        Assert.Contains("\"action\"", GroundedPromptBuilder.DecisionSchema, StringComparison.Ordinal);
        Assert.Contains("\"spoken_text\"", GroundedPromptBuilder.DecisionSchema, StringComparison.Ordinal);
        Assert.Contains("\"additionalProperties\": false", GroundedPromptBuilder.DecisionSchema, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"action":"reply","spoken_text":"It is the printer.","reason":"Grounded printer answer."}""", GroundedReplyAction.Reply, "It is the printer.")]
    [InlineData("""{"action":"hang_up","spoken_text":null,"reason":"Remote party said goodbye."}""", GroundedReplyAction.HangUp, null)]
    public void DecisionParser_AcceptsStrictValidDecisions(
        string json,
        GroundedReplyAction expectedAction,
        string? expectedSpokenText)
    {
        var decision = GroundedModelDecisionParser.Parse(json);

        Assert.Equal(expectedAction, decision.Action);
        Assert.Equal(expectedSpokenText, decision.SpokenText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I cannot comply")]
    [InlineData("""{"action":"reply","spoken_text":"","reason":"x"}""")]
    [InlineData("""{"action":"invent","spoken_text":"x","reason":"x"}""")]
    [InlineData("""{"action":"reply","spoken_text":"x","reason":"x","extra":"x"}""")]
    public void DecisionParser_RejectsEmptyRefusedAndInvalidOutput(string? output)
    {
        Assert.Throws<GroundedReplyException>(() => GroundedModelDecisionParser.Parse(output));
    }

    [Fact]
    public async Task OpeningLine_UsesSpeechAndMediaWithoutCallingModel()
    {
        var call = new FakeCallSession();
        var media = (FakeMediaTransport)call.CallerMediaTransport;
        var model = new FakeReplyGenerator();
        var speech = new FakeSpeechPipeline();
        var monitor = new FakeAudioMonitor();
        await using var orchestrator = CreateOrchestrator(call, model, speech, monitor);

        await orchestrator.StartAsync();

        Assert.Equal(0, model.CallCount);
        Assert.Equal([CreateDraft().OpeningLine], speech.SynthesizedTexts);
        Assert.Single(media.SentFrames);
        Assert.Equal(AcsMediaTransport.PcmFrameBytes, media.SentFrames[0].Length);
        Assert.Equal(CallerActivityState.Listening, orchestrator.ActivityState);
        Assert.Equal(TranscriptSpeaker.Caller, Assert.Single(orchestrator.Transcript).Speaker);
    }

    [Fact]
    public async Task FinalTurn_FramesAndPadsAudioAtTwentyMillisecondCadence()
    {
        var call = new FakeCallSession();
        var media = (FakeMediaTransport)call.CallerMediaTransport;
        var model = new FakeReplyGenerator
        {
            Decision = new GroundedModelDecision(GroundedReplyAction.Reply, "A concise reply.", "Grounded."),
        };
        var speech = new FakeSpeechPipeline
        {
            Synthesis = text => text == CreateDraft().OpeningLine ? [1, 2] : new byte[650],
        };
        var monitor = new FakeAudioMonitor();
        var timeProvider = new CapturingTimeProvider();
        await using var orchestrator = CreateOrchestrator(call, model, speech, monitor, timeProvider);
        await orchestrator.StartAsync();
        media.ClearSent();
        monitor.Clear();

        speech.EmitFinal("What happened?");

        await EventuallyAsync(() => media.SentFrames.Count == 2);

        Assert.All(media.SentFrames, frame => Assert.Equal(AcsMediaTransport.PcmFrameBytes, frame.Length));
        Assert.Equal(0, media.SentFrames[1][10]);
        Assert.Contains(TimeSpan.FromMilliseconds(20), timeProvider.ScheduledDueTimes);
        Assert.Equal(1, model.MaximumConcurrentCalls);
        Assert.Equal(2, monitor.Frames.Count);
    }

    [Fact]
    public async Task InterimAndDuplicateFinalSegment_AppendExpectedTranscriptAndProcessOnce()
    {
        var call = new FakeCallSession();
        var model = new FakeReplyGenerator
        {
            Decision = new GroundedModelDecision(GroundedReplyAction.Reply, null, "No audio needed."),
        };
        var speech = new FakeSpeechPipeline();
        await using var orchestrator = CreateOrchestrator(
            call,
            model,
            speech,
            new FakeAudioMonitor());
        await orchestrator.StartAsync();

        speech.EmitInterim("Can you");
        speech.EmitFinal("Can you help?", "segment-1");
        speech.EmitFinal("Can you help?", "segment-1");

        await EventuallyAsync(() => model.CallCount == 1);

        Assert.Contains(
            orchestrator.Transcript,
            item => item.Speaker == TranscriptSpeaker.ServiceDesk
                && item.Status == TranscriptStatus.Interim
                && item.Text == "Can you");
        Assert.Equal(
            1,
            orchestrator.Transcript.Count(
                item => item.Speaker == TranscriptSpeaker.ServiceDesk
                    && item.Status == TranscriptStatus.Final
                    && item.Text == "Can you help?"));
    }

    [Fact]
    public async Task DistinctFinalSegmentsWithSameText_AreEachProcessed()
    {
        var call = new FakeCallSession();
        var model = new FakeReplyGenerator
        {
            Decision = new GroundedModelDecision(GroundedReplyAction.Reply, null, "No audio needed."),
        };
        var speech = new FakeSpeechPipeline();
        await using var orchestrator = CreateOrchestrator(
            call,
            model,
            speech,
            new FakeAudioMonitor());
        await orchestrator.StartAsync();

        speech.EmitFinal("Can you help?", "segment-1");
        speech.EmitFinal("What details do you need?", "segment-2");
        speech.EmitFinal("Can you help?", "segment-3");

        await EventuallyAsync(() => model.CallCount == 3);

        Assert.Equal(
            2,
            orchestrator.Transcript.Count(
                item => item.Speaker == TranscriptSpeaker.ServiceDesk
                    && item.Status == TranscriptStatus.Final
                    && item.Text == "Can you help?"));
        Assert.Equal(1, model.MaximumConcurrentCalls);
    }

    [Fact]
    public async Task InboundServiceDeskAudio_MonitorsOnlyNonSilentFramesAndFeedsRecognition()
    {
        var call = new FakeCallSession();
        var media = (FakeMediaTransport)call.CallerMediaTransport;
        var speech = new FakeSpeechPipeline();
        var monitor = new FakeAudioMonitor();
        await using var orchestrator = CreateOrchestrator(
            call,
            new FakeReplyGenerator(),
            speech,
            monitor);
        await orchestrator.StartAsync();
        monitor.Clear();

        media.PublishInbound(CreateTone(1), isSilent: false);
        media.PublishInbound(CreateTone(2), isSilent: true);

        await EventuallyAsync(() => speech.WrittenFrames.Count == 1);
        await Task.Delay(50);

        Assert.Equal(AcsMediaTransport.PcmFrameBytes, speech.WrittenFrames[0].Length);
        Assert.Equal(CreateTone(1), Assert.Single(monitor.Frames));
    }

    [Fact]
    public async Task InboundMonitoring_IsSuppressedOnlyWhileCallerPlaybackIsActive()
    {
        var call = new FakeCallSession();
        var media = (FakeMediaTransport)call.CallerMediaTransport;
        media.BlockOnSendCall = 1;
        var speech = new FakeSpeechPipeline
        {
            Synthesis = _ => new byte[AcsMediaTransport.PcmFrameBytes],
        };
        var monitor = new FakeAudioMonitor();
        await using var orchestrator = CreateOrchestrator(
            call,
            new FakeReplyGenerator(),
            speech,
            monitor);
        var start = orchestrator.StartAsync();

        await media.BlockedSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        monitor.Clear();
        media.PublishInbound(CreateTone(7), isSilent: false);
        await EventuallyAsync(() => speech.WrittenFrames.Count == 1);
        await Task.Delay(50);

        // The caller owns the monitor FIFO while its own audio is playing out.
        Assert.Empty(monitor.Frames);

        media.ReleaseBlockedSend();
        await start;

        media.PublishInbound(CreateTone(9), isSilent: false);

        await EventuallyAsync(() => monitor.Frames.Count == 1);
        Assert.Equal(CreateTone(9), monitor.Frames[0]);
        Assert.Equal(2, speech.WrittenFrames.Count);
    }

    [Fact]
    public async Task OutboundCallerAudio_IsAlwaysMonitored()
    {
        var call = new FakeCallSession();
        var media = (FakeMediaTransport)call.CallerMediaTransport;
        var speech = new FakeSpeechPipeline
        {
            Synthesis = _ => new byte[AcsMediaTransport.PcmFrameBytes],
        };
        var monitor = new FakeAudioMonitor();
        await using var orchestrator = CreateOrchestrator(
            call,
            new FakeReplyGenerator(),
            speech,
            monitor);

        await orchestrator.StartAsync();

        Assert.Equal(new byte[AcsMediaTransport.PcmFrameBytes], Assert.Single(monitor.Frames));
    }

    [Fact]
    public async Task InvalidModelDecision_FaultsConversationWithoutSpeakingFallbackContent()
    {
        var call = new FakeCallSession();
        var model = new FakeReplyGenerator
        {
            Exception = new GroundedReplyException("Invalid structured output."),
        };
        var speech = new FakeSpeechPipeline();
        var monitor = new FakeAudioMonitor();
        await using var orchestrator = CreateOrchestrator(call, model, speech, monitor);
        await orchestrator.StartAsync();

        speech.EmitFinal("What is your asset number?");

        await EventuallyAsync(() => orchestrator.ActivityState == CallerActivityState.Faulted);
        await EventuallyAsync(() => speech.Disposed && monitor.Disposed);

        Assert.Equal(1, call.HangUpCalls);
        Assert.Single(speech.SynthesizedTexts);
        Assert.Equal(1, speech.DisposeCalls);
        Assert.Equal(1, monitor.DisposeCalls);
        Assert.Contains(
            orchestrator.Transcript,
            item => item.Speaker == TranscriptSpeaker.System
                && item.Text == "The grounded caller decision was invalid.");
    }

    [Fact]
    public async Task FinalTurns_AreSerializedWithoutOverlappingModelOrSynthesis()
    {
        var call = new FakeCallSession();
        var model = new FakeReplyGenerator
        {
            BlockFirstCall = true,
            Decision = new GroundedModelDecision(GroundedReplyAction.Reply, null, "Grounded."),
        };
        var speech = new FakeSpeechPipeline();
        await using var orchestrator = CreateOrchestrator(call, model, speech, new FakeAudioMonitor());
        await orchestrator.StartAsync();

        speech.EmitFinal("First question");
        await model.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        speech.EmitFinal("Second question");
        await Task.Delay(50);
        Assert.Equal(1, model.CallCount);

        model.ReleaseFirstCall();
        await EventuallyAsync(() => model.CallCount == 2);

        Assert.Equal(1, model.MaximumConcurrentCalls);
        Assert.Equal(1, speech.MaximumConcurrentSyntheses);
    }

    [Fact]
    public async Task InterimBargeIn_StopsGenerationWithoutFaultingAndProcessesTheFinalUtterance()
    {
        var call = new FakeCallSession();
        var media = (FakeMediaTransport)call.CallerMediaTransport;
        var model = new FakeReplyGenerator
        {
            DecisionFactory = callCount => callCount == 1
                ? new GroundedModelDecision(GroundedReplyAction.Reply, "Long answer.", "Grounded.")
                : new GroundedModelDecision(GroundedReplyAction.Reply, null, "Grounded replacement."),
        };
        var speech = new FakeSpeechPipeline
        {
            Synthesis = text => text == CreateDraft().OpeningLine ? [1, 2] : new byte[1_920],
        };
        await using var orchestrator = CreateOrchestrator(call, model, speech, new FakeAudioMonitor());
        await orchestrator.StartAsync();
        media.ClearSent();
        media.BlockOnSendCall = 3;

        speech.EmitFinal("Please explain.");
        await media.BlockedSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        speech.EmitInterim("Actually");

        await EventuallyAsync(() => media.StopCalls == 1);
        speech.EmitFinal("Please address the replacement question.", "barge-in-final");
        await EventuallyAsync(() => model.CallCount == 2);
        await Task.Delay(50);

        Assert.Single(media.SentFrames);
        Assert.Equal(0, media.SendsAfterStop);
        Assert.Equal(0, call.HangUpCalls);
        Assert.Equal(CallerActivityState.Listening, orchestrator.ActivityState);
    }

    [Fact]
    public async Task HangUpDecision_SpeaksFinalTextThenHangsUpOnceAndPublishesOrder()
    {
        var call = new FakeCallSession();
        var media = (FakeMediaTransport)call.CallerMediaTransport;
        var model = new FakeReplyGenerator
        {
            Decision = new GroundedModelDecision(GroundedReplyAction.HangUp, "Thank you, goodbye.", "Goodbye."),
        };
        var speech = new FakeSpeechPipeline();
        var activities = new List<CallerActivityState>();
        await using var orchestrator = CreateOrchestrator(call, model, speech, new FakeAudioMonitor());
        orchestrator.ActivityChanged += (_, change) => activities.Add(change.CurrentState);
        await orchestrator.StartAsync();
        media.ClearSent();

        speech.EmitFinal("Goodbye.");

        await EventuallyAsync(() => call.HangUpCalls == 1);

        Assert.Contains("Thank you, goodbye.", speech.SynthesizedTexts);
        Assert.NotEmpty(media.SentFrames);
        Assert.Equal(1, call.HangUpCalls);
        Assert.Contains(CallerActivityState.Ending, activities);
        Assert.Equal(CallerActivityState.Ended, orchestrator.ActivityState);
    }

    [Theory]
    [InlineData(CallSessionState.Ended)]
    [InlineData(CallSessionState.Faulted)]
    public async Task RemoteTerminalState_DisposesSpeechAndLocalMonitoringExactlyOnce(
        CallSessionState terminalState)
    {
        var call = new FakeCallSession();
        var speech = new FakeSpeechPipeline();
        var monitor = new FakeAudioMonitor();
        await using var orchestrator = CreateOrchestrator(
            call,
            new FakeReplyGenerator(),
            speech,
            monitor);
        await orchestrator.StartAsync();

        call.RaiseState(terminalState);

        await EventuallyAsync(() => speech.Disposed && monitor.Disposed);

        Assert.Equal(
            terminalState == CallSessionState.Faulted ? CallerActivityState.Faulted : CallerActivityState.Ended,
            orchestrator.ActivityState);
        Assert.Equal(1, speech.DisposeCalls);
        Assert.Equal(1, monitor.DisposeCalls);
    }

    [Fact]
    public async Task MediaDisconnect_CancelsWorkWithoutRequestingAnotherHangUp()
    {
        var call = new FakeCallSession();
        var media = (FakeMediaTransport)call.CallerMediaTransport;
        var speech = new FakeSpeechPipeline();
        var monitor = new FakeAudioMonitor();
        await using var orchestrator = CreateOrchestrator(
            call,
            new FakeReplyGenerator(),
            speech,
            monitor);
        await orchestrator.StartAsync();

        media.Disconnect();

        await EventuallyAsync(() => speech.Disposed && monitor.Disposed);

        Assert.Equal(0, call.HangUpCalls);
        Assert.Equal(CallerActivityState.Ended, orchestrator.ActivityState);
        Assert.Equal(1, speech.DisposeCalls);
        Assert.Equal(1, monitor.DisposeCalls);
    }

    [Fact]
    public async Task ExternalManualHangUp_CancelsCallerResources()
    {
        var call = new FakeCallSession();
        var speech = new FakeSpeechPipeline();
        var monitor = new FakeAudioMonitor();
        await using var orchestrator = CreateOrchestrator(
            call,
            new FakeReplyGenerator(),
            speech,
            monitor);
        await orchestrator.StartAsync();

        await call.HangUpAsync();

        await EventuallyAsync(() => speech.Disposed && monitor.Disposed);

        Assert.Equal(1, call.HangUpCalls);
        Assert.Equal(CallerActivityState.Ended, orchestrator.ActivityState);
        Assert.Equal(1, speech.DisposeCalls);
        Assert.Equal(1, monitor.DisposeCalls);
    }

    [Fact]
    public async Task Disposal_CancelsResourcesAndRequestsOneManualHangUp()
    {
        var call = new FakeCallSession();
        var speech = new FakeSpeechPipeline();
        var monitor = new FakeAudioMonitor();
        var orchestrator = CreateOrchestrator(call, new FakeReplyGenerator(), speech, monitor);
        await orchestrator.StartAsync();

        await orchestrator.DisposeAsync();

        Assert.Equal(1, call.HangUpCalls);
        Assert.True(speech.Disposed);
        Assert.True(monitor.Disposed);
        Assert.Equal(1, speech.DisposeCalls);
        Assert.Equal(1, monitor.DisposeCalls);
    }

    [Fact]
    public async Task TerminalCleanup_FaultingRecognitionStop_DisposesOwnedResourcesAndSurfacesFailure()
    {
        var call = new FakeCallSession();
        var speech = new FakeSpeechPipeline
        {
            StopRecognition = () => Task.FromException(new InvalidOperationException("Native stop failed.")),
        };
        var monitor = new FakeAudioMonitor();
        var orchestrator = CreateOrchestrator(call, new FakeReplyGenerator(), speech, monitor);
        await orchestrator.StartAsync();

        var cleanup = orchestrator.DisposeAsync().AsTask();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.True(orchestrator.HasTerminalCleanupFailure);
        Assert.Equal(CallerActivityState.Faulted, orchestrator.ActivityState);
        Assert.Contains(
            orchestrator.Transcript,
            item => item.Speaker == TranscriptSpeaker.System
                && item.Text == "The caller conversation cleanup failed.");
        Assert.Equal(1, speech.StopCalls);
        Assert.Equal(1, speech.DisposeCalls);
        Assert.Equal(1, monitor.StopCalls);
        Assert.Equal(1, monitor.DisposeCalls);
    }

    [Fact]
    public async Task TerminalCleanup_NeverCompletingRecognitionStop_DisposesOwnedResourcesWithinTheDeadline()
    {
        var call = new FakeCallSession();
        var neverCompletingStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var speech = new FakeSpeechPipeline
        {
            StopRecognition = () => neverCompletingStop.Task,
        };
        var monitor = new FakeAudioMonitor();
        var orchestrator = CreateOrchestrator(
            call,
            new FakeReplyGenerator(),
            speech,
            monitor,
            recognitionStopTimeout: TimeSpan.FromMilliseconds(50));
        await orchestrator.StartAsync();

        var cleanup = orchestrator.DisposeAsync().AsTask();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsType<TimeoutException>(failure.InnerException);
        Assert.True(orchestrator.HasTerminalCleanupFailure);
        Assert.Equal(CallerActivityState.Faulted, orchestrator.ActivityState);
        Assert.Equal(1, speech.StopCalls);
        Assert.Equal(1, speech.DisposeCalls);
        Assert.Equal(1, monitor.StopCalls);
        Assert.Equal(1, monitor.DisposeCalls);
    }

    [Fact]
    public void ConversationMessage_OmitsInterimRecognitionFragmentsAndKeepsFinalHistory()
    {
        var history = new[]
        {
            new TranscriptTurn(
                DateTimeOffset.Parse("2026-08-26T18:00:00Z"),
                TranscriptSpeaker.Caller,
                "Hallo, hier spricht Maya.",
                TranscriptStatus.Final),
            new TranscriptTurn(
                DateTimeOffset.Parse("2026-08-26T18:00:05Z"),
                TranscriptSpeaker.ServiceDesk,
                "Which dev",
                TranscriptStatus.Interim),
            new TranscriptTurn(
                DateTimeOffset.Parse("2026-08-26T18:00:06Z"),
                TranscriptSpeaker.ServiceDesk,
                "Which device has the problem?",
                TranscriptStatus.Final),
        };

        var conversation = GroundedPromptBuilder.BuildConversationMessage(history);

        Assert.DoesNotContain("Interim", conversation, StringComparison.Ordinal);
        Assert.DoesNotContain("Which dev\r", conversation, StringComparison.Ordinal);
        Assert.DoesNotContain("Which dev\n", conversation, StringComparison.Ordinal);
        Assert.Contains("Hallo, hier spricht Maya.", conversation, StringComparison.Ordinal);
        Assert.Contains("Which device has the problem?", conversation, StringComparison.Ordinal);
        Assert.Equal(2, conversation.Split("] ").Length - 1);
    }

    [Fact]
    public async Task ModelHistory_ExcludesInterimTurnsWhileTheUiTranscriptKeepsThem()
    {
        var call = new FakeCallSession();
        var model = new FakeReplyGenerator
        {
            Decision = new GroundedModelDecision(GroundedReplyAction.Reply, null, "No audio needed."),
        };
        var speech = new FakeSpeechPipeline();
        await using var orchestrator = CreateOrchestrator(
            call,
            model,
            speech,
            new FakeAudioMonitor());
        await orchestrator.StartAsync();

        speech.EmitInterim("Which dev");
        speech.EmitFinal("Which device has the problem?", "segment-1");

        await EventuallyAsync(() => model.CallCount == 1);

        var modelHistory = Assert.Single(model.Transcripts);
        Assert.All(modelHistory, turn => Assert.Equal(TranscriptStatus.Final, turn.Status));
        Assert.DoesNotContain(modelHistory, turn => turn.Text == "Which dev");
        Assert.Contains(modelHistory, turn => turn.Text == "Which device has the problem?");
        Assert.Contains(modelHistory, turn => turn.Text == CreateDraft().OpeningLine);
        Assert.Contains(
            orchestrator.Transcript,
            turn => turn.Status == TranscriptStatus.Interim && turn.Text == "Which dev");

        var conversation = GroundedPromptBuilder.BuildConversationMessage(modelHistory);
        Assert.DoesNotContain("Which dev\r", conversation, StringComparison.Ordinal);
        Assert.DoesNotContain("Which dev\n", conversation, StringComparison.Ordinal);
        Assert.Contains("Which device has the problem?", conversation, StringComparison.Ordinal);
    }

    private static ScriptedCallerOrchestrator CreateOrchestrator(
        FakeCallSession call,
        FakeReplyGenerator model,
        FakeSpeechPipeline speech,
        FakeAudioMonitor monitor,
        TimeProvider? timeProvider = null,
        TimeSpan? recognitionStopTimeout = null) =>
        new(
            CallerScriptSnapshot.FromDraft(CreateDraft()),
            call,
            model,
            speech,
            monitor,
            new ScriptedCallerOrchestratorOptions
            {
                ConnectionTimeout = TimeSpan.FromSeconds(2),
                MediaReadyTimeout = TimeSpan.FromSeconds(2),
                RecognitionStartTimeout = TimeSpan.FromSeconds(2),
                RecognitionStopTimeout = recognitionStopTimeout ?? TimeSpan.FromSeconds(2),
                SynthesisTimeout = TimeSpan.FromSeconds(2),
                GenerationDrainTimeout = TimeSpan.FromSeconds(2),
                MediaOperationTimeout = TimeSpan.FromSeconds(2),
            },
            timeProvider);

    private static CallerScriptDraft CreateDraft() => new()
    {
        Name = "DE printer",
        Locale = "de-DE",
        Voice = "de-DE-KatjaNeural",
        OpeningLine = "Hallo, hier spricht Maya.",
        Identity = "Maya",
        Background = "The front-office printer showed offline after lunch.",
        Reason = "Need a status check and escalation.",
        Urgency = "High",
        CallbackNumber = "+4915112345678",
        AdditionalDetails = "Call back after the printer queue is cleared.",
    };

    private static byte[] CreateTone(byte value)
    {
        var frame = new byte[AcsMediaTransport.PcmFrameBytes];
        Array.Fill(frame, value);
        return frame;
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected asynchronous condition was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeCallSession : ICallerCallSession
    {
        public Task ConnectionReady => Task.CompletedTask;

        public CallSessionState State { get; private set; } = CallSessionState.Connected;

        public ICallMediaTransport CallerMediaTransport { get; } = new FakeMediaTransport();

        public int HangUpCalls { get; private set; }

        public event EventHandler<CallStateChange>? StateChanged;

        public Task HangUpAsync(CancellationToken cancellationToken = default)
        {
            HangUpCalls++;
            RaiseState(CallSessionState.Ending);
            RaiseState(CallSessionState.Ended);
            return Task.CompletedTask;
        }

        public void RaiseState(CallSessionState nextState)
        {
            var change = new CallStateChange(State, nextState, DateTimeOffset.UtcNow, "test");
            State = nextState;
            StateChanged?.Invoke(this, change);
        }
    }

    private sealed class FakeMediaTransport : ICallMediaTransport
    {
        private readonly Channel<AcsInboundAudioFrame> _inbound = Channel.CreateUnbounded<AcsInboundAudioFrame>();
        private readonly TaskCompletionSource _disconnected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _blockRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HashSet<long> _stoppedGenerations = [];
        private long _nextGeneration;
        private int _sendCalls;
        private int _stopped;

        public Task ConnectionReady => Task.CompletedTask;

        public Task Disconnected => _disconnected.Task;

        public ChannelReader<AcsInboundAudioFrame> InboundFrames => _inbound.Reader;

        public List<byte[]> SentFrames { get; } = [];

        public int BlockOnSendCall { get; set; }

        public int StopCalls { get; private set; }

        public int SendsAfterStop { get; private set; }

        public TaskCompletionSource BlockedSendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long CreateAudioGeneration() => Interlocked.Increment(ref _nextGeneration);

        public async Task SendAudioAsync(
            long generation,
            ReadOnlyMemory<byte> pcm16KMono,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(AcsMediaTransport.PcmFrameBytes, pcm16KMono.Length);
            var call = Interlocked.Increment(ref _sendCalls);
            if (BlockOnSendCall == call)
            {
                BlockedSendStarted.TrySetResult();
                await _blockRelease.Task.WaitAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (_stoppedGenerations)
            {
                if (_stoppedGenerations.Contains(generation))
                {
                    if (Volatile.Read(ref _stopped) != 0)
                    {
                        SendsAfterStop++;
                    }

                    return;
                }

                if (Volatile.Read(ref _stopped) != 0)
                {
                    SendsAfterStop++;
                }

                SentFrames.Add(pcm16KMono.ToArray());
            }
        }

        public Task StopAudioAsync(long generation, CancellationToken cancellationToken = default)
        {
            lock (_stoppedGenerations)
            {
                _stoppedGenerations.Add(generation);
            }

            StopCalls++;
            Volatile.Write(ref _stopped, 1);
            _blockRelease.TrySetResult();
            return Task.CompletedTask;
        }

        public void ClearSent() => SentFrames.Clear();

        public void ReleaseBlockedSend() => _blockRelease.TrySetResult();

        public void PublishInbound(byte[] pcm, bool isSilent) =>
            _inbound.Writer.TryWrite(new AcsInboundAudioFrame(pcm, "test", null, isSilent));

        public void Disconnect() => _disconnected.TrySetResult();
    }

    private sealed class FakeReplyGenerator : IGroundedReplyGenerator
    {
        private readonly TaskCompletionSource _firstCallRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeCalls;

        public GroundedModelDecision Decision { get; init; } =
            new(GroundedReplyAction.Reply, null, "Grounded.");

        public bool BlockFirstCall { get; init; }

        public Exception? Exception { get; init; }

        public Func<int, GroundedModelDecision>? DecisionFactory { get; init; }

        public int CallCount { get; private set; }

        public int MaximumConcurrentCalls { get; private set; }

        public TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<IReadOnlyList<TranscriptTurn>> Transcripts { get; } = [];

        public async Task<GroundedModelDecision> GenerateAsync(
            CallerScriptSnapshot script,
            IReadOnlyList<TranscriptTurn> transcript,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Transcripts.Add(transcript);
            var active = Interlocked.Increment(ref _activeCalls);
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, active);
            try
            {
                if (Exception is not null)
                {
                    throw Exception;
                }

                if (BlockFirstCall && CallCount == 1)
                {
                    FirstCallStarted.TrySetResult();
                    await _firstCallRelease.Task.WaitAsync(cancellationToken);
                }

                return DecisionFactory?.Invoke(CallCount) ?? Decision;
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        public void ReleaseFirstCall() => _firstCallRelease.TrySetResult();
    }

    private sealed class FakeSpeechPipeline : ISpeechPipeline
    {
        private int _activeSyntheses;
        private int _nextSegmentId;

        public event EventHandler<SpeechRecognitionUpdate>? RecognitionUpdated;

        public Func<string, byte[]> Synthesis { get; init; } = _ => [1, 2];

        public List<string> SynthesizedTexts { get; } = [];

        public List<byte[]> WrittenFrames { get; } = [];

        public int StopCalls { get; private set; }

        public Func<Task>? StopRecognition { get; init; }

        public int MaximumConcurrentSyntheses { get; private set; }

        public bool Disposed { get; private set; }

        public int DisposeCalls { get; private set; }

        public Task StartRecognitionAsync(string locale, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask WritePcmAsync(ReadOnlyMemory<byte> pcm16KMono, CancellationToken cancellationToken)
        {
            WrittenFrames.Add(pcm16KMono.ToArray());
            return ValueTask.CompletedTask;
        }

        public Task<byte[]> SynthesizeAsync(string voice, string text, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _activeSyntheses);
            MaximumConcurrentSyntheses = Math.Max(MaximumConcurrentSyntheses, active);
            try
            {
                SynthesizedTexts.Add(text);
                return Task.FromResult(Synthesis(text));
            }
            finally
            {
                Interlocked.Decrement(ref _activeSyntheses);
            }
        }

        public Task StopRecognitionAsync()
        {
            StopCalls++;
            return StopRecognition?.Invoke() ?? Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public void EmitInterim(string text) =>
            RecognitionUpdated?.Invoke(
                this,
                new SpeechRecognitionUpdate(
                    text,
                    IsFinal: false,
                    Error: null,
                    SegmentId: $"interim-{Interlocked.Increment(ref _nextSegmentId)}"));

        public void EmitFinal(string text, string? segmentId = null) =>
            RecognitionUpdated?.Invoke(
                this,
                new SpeechRecognitionUpdate(
                    text,
                    IsFinal: true,
                    Error: null,
                    SegmentId: segmentId ?? $"final-{Interlocked.Increment(ref _nextSegmentId)}"));
    }

    private sealed class FakeAudioMonitor : IAudioMonitor
    {
        public bool IsMuted { get; set; }

        public List<byte[]> Frames { get; } = [];

        public int StopCalls { get; private set; }

        public bool Disposed { get; private set; }

        public int DisposeCalls { get; private set; }

        public event EventHandler<AudioMonitorFault>? Faulted
        {
            add { }
            remove { }
        }

        public bool TryMonitor(ReadOnlyMemory<byte> pcm16KMono)
        {
            if (!IsMuted)
            {
                Frames.Add(pcm16KMono.ToArray());
            }

            return true;
        }

        public Task StopAsync()
        {
            StopCalls++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public void Clear() => Frames.Clear();
    }

    private sealed class CapturingTimeProvider : TimeProvider
    {
        public ConcurrentBag<TimeSpan> ScheduledDueTimes { get; } = [];

        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();

        public override long GetTimestamp() => TimeProvider.System.GetTimestamp();

        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ScheduledDueTimes.Add(dueTime);
            return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
        }
    }
}
