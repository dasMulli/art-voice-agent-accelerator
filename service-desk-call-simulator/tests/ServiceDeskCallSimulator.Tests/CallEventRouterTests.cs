using ServiceDeskCallSimulator.Calls;
using ServiceDeskCallSimulator.Conversation;
using ServiceDeskCallSimulator.Monitoring;
using ServiceDeskCallSimulator.PhoneNumbers;
using ServiceDeskCallSimulator.Presets;
using ServiceDeskCallSimulator.UI;

namespace ServiceDeskCallSimulator.Tests;

public sealed class CallEventRouterTests
{
    private static SimulatorController ReadyController()
    {
        var controller = new SimulatorController();
        var discovery = new PhoneNumberSelectionResult(["+43800223359"], "+43800223359");
        controller.CompleteInitialization(discovery, "+33801150311", ["[EN] Printer not working"], null, null);
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

    private static (SimulatorController Controller, TranscriptPresenter Presenter, CallGenerationGate Gate, List<string> Diagnostics, CallEventRouter Router, long Generation)
        CreateRouter()
    {
        var controller = ReadyController();
        controller.BeginDial();
        var presenter = new TranscriptPresenter();
        var gate = new CallGenerationGate();
        var generation = gate.Advance();
        var diagnostics = new List<string>();
        var router = new CallEventRouter(
            controller,
            presenter,
            gate,
            generation,
            diagnostics.Add,
            new InlineUiEventDispatcher());
        return (controller, presenter, gate, diagnostics, router, generation);
    }

    private static TranscriptTurn Turn(TranscriptSpeaker speaker, string text, TranscriptStatus status) =>
        new(DateTimeOffset.UtcNow, speaker, text, status);

    [Fact]
    public void HandleCallStateChanged_UpdatesControllerAndRaisesCallEndedOnEnded()
    {
        var (controller, _, _, _, router, _) = CreateRouter();
        CallStateChange? ended = null;
        router.CallEnded += change => ended = change;

        router.HandleCallStateChanged(null, new CallStateChange(CallSessionState.Dialing, CallSessionState.Connected, DateTimeOffset.UtcNow, "connected"));
        Assert.Equal(CallSessionState.Connected, controller.State.CallState);
        Assert.Null(ended);

        router.HandleCallStateChanged(null, new CallStateChange(CallSessionState.Connected, CallSessionState.Ended, DateTimeOffset.UtcNow, "hang-up"));
        Assert.Equal(CallSessionState.Ended, controller.State.CallState);
        Assert.NotNull(ended);
        Assert.Equal("hang-up", ended!.Reason);
    }

    [Fact]
    public void HandleCallStateChanged_RaisesCallEndedOnFaulted()
    {
        var (_, _, _, _, router, _) = CreateRouter();
        var endedCount = 0;
        router.CallEnded += _ => endedCount++;

        router.HandleCallStateChanged(null, new CallStateChange(CallSessionState.Connected, CallSessionState.Faulted, DateTimeOffset.UtcNow, "media failure"));

        Assert.Equal(1, endedCount);
    }

    [Fact]
    public void HandleCallStateChanged_TerminalRaceRaisesCallEndedOnlyOnce()
    {
        var (_, _, _, _, router, _) = CreateRouter();
        var endedCount = 0;
        router.CallEnded += _ => endedCount++;

        router.HandleCallStateChanged(null, new CallStateChange(
            CallSessionState.Connected,
            CallSessionState.Ended,
            DateTimeOffset.UtcNow,
            "disconnect"));
        router.HandleCallStateChanged(null, new CallStateChange(
            CallSessionState.Ending,
            CallSessionState.Faulted,
            DateTimeOffset.UtcNow,
            "cleanup fault"));

        Assert.Equal(1, endedCount);
    }

    [Fact]
    public void HandleTranscriptUpdated_ClearsExistingTranscriptOnlyOnFirstEventOfNewCall()
    {
        var (controller, presenter, gate, _, router, _) = CreateRouter();
        var clearedRaised = 0;
        presenter.Cleared += (_, _) => clearedRaised++;

        // Simulate a retained transcript from a previous, already-completed call.
        presenter.Apply(Turn(TranscriptSpeaker.System, "Previous call ended.", TranscriptStatus.Final));
        Assert.Single(presenter.Lines);

        router.HandleTranscriptUpdated(null, Turn(TranscriptSpeaker.Caller, "Opening line.", TranscriptStatus.Final));

        // The first event of the new call clears the old transcript (raising Cleared exactly
        // once, so a UI display stays in lockstep) before applying itself.
        Assert.Equal(1, clearedRaised);
        Assert.Single(presenter.Lines);
        Assert.Equal("Opening line.", presenter.Lines[0].Text);
        Assert.True(controller.State.HasTranscript);

        router.HandleTranscriptUpdated(null, Turn(TranscriptSpeaker.ServiceDesk, "How can I help?", TranscriptStatus.Final));
        Assert.Equal(1, clearedRaised); // not cleared again for the second event of the same call
        Assert.Equal(2, presenter.Lines.Count);
        _ = gate;
    }

    [Fact]
    public void HandleActivityChanged_UpdatesController()
    {
        var (controller, _, _, _, router, _) = CreateRouter();
        router.HandleActivityChanged(null, new CallerActivityChange(CallerActivityState.Idle, CallerActivityState.Listening, DateTimeOffset.UtcNow, "listening"));
        Assert.Equal(CallerActivityState.Listening, controller.State.Activity);
    }

    [Fact]
    public void HandleAudioMonitorFaulted_ReportsDiagnosticsOnlyAndNeverEndsTheCall()
    {
        var (controller, _, _, diagnostics, router, _) = CreateRouter();
        var endedRaised = false;
        router.CallEnded += _ => endedRaised = true;

        router.HandleAudioMonitorFaulted(null, new AudioMonitorFault("Open", "Device unavailable."));

        Assert.Single(diagnostics);
        Assert.Contains("Device unavailable.", diagnostics[0]);
        Assert.False(endedRaised);
        Assert.Equal(CallSessionState.Dialing, controller.State.CallState); // unchanged by the fault
    }

    [Fact]
    public void AllHandlers_IgnoreEventsForARetiredGeneration()
    {
        var (controller, presenter, gate, diagnostics, router, generation) = CreateRouter();
        gate.Retire(generation);

        router.HandleCallStateChanged(null, new CallStateChange(CallSessionState.Dialing, CallSessionState.Connected, DateTimeOffset.UtcNow, "late"));
        router.HandleTranscriptUpdated(null, Turn(TranscriptSpeaker.Caller, "Late line.", TranscriptStatus.Final));
        router.HandleActivityChanged(null, new CallerActivityChange(CallerActivityState.Idle, CallerActivityState.Speaking, DateTimeOffset.UtcNow, "late"));
        router.HandleAudioMonitorFaulted(null, new AudioMonitorFault("Open", "late"));

        Assert.Equal(CallSessionState.Dialing, controller.State.CallState); // unchanged
        Assert.Empty(presenter.Lines);
        Assert.Null(controller.State.Activity);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void EventsFromANewerGeneration_AreNotFilteredByAnOlderRouter()
    {
        var controller = ReadyController();
        controller.BeginDial();
        var presenter = new TranscriptPresenter();
        var gate = new CallGenerationGate();
        var firstGeneration = gate.Advance();
        var firstRouter = new CallEventRouter(
            controller,
            presenter,
            gate,
            firstGeneration,
            _ => { },
            new InlineUiEventDispatcher());

        // A new call starts before the first call's late event arrives.
        var secondGeneration = gate.Advance();
        var secondRouter = new CallEventRouter(
            controller,
            presenter,
            gate,
            secondGeneration,
            _ => { },
            new InlineUiEventDispatcher());

        firstRouter.HandleTranscriptUpdated(null, Turn(TranscriptSpeaker.Caller, "Stale.", TranscriptStatus.Final));
        Assert.Empty(presenter.Lines);

        secondRouter.HandleTranscriptUpdated(null, Turn(TranscriptSpeaker.Caller, "Current.", TranscriptStatus.Final));
        Assert.Single(presenter.Lines);
        Assert.Equal("Current.", presenter.Lines[0].Text);
    }

    [Fact]
    public async Task ConcurrentWorkerEventsAndTerminalEvent_AreDiscardedWhenTeardownRetiresBeforeUiDrain()
    {
        var (controller, presenter, gate, diagnostics, _, generation) = CreateRouter();
        var synchronizationContext = new QueuedSynchronizationContext();
        var router = new CallEventRouter(
            controller,
            presenter,
            gate,
            generation,
            diagnostics.Add,
            new SerializedUiEventDispatcher(synchronizationContext));
        var terminalNotifications = 0;
        router.CallEnded += _ => terminalNotifications++;

        var producerTasks = Enumerable.Range(0, 20)
            .Select(index => Task.Run(() =>
            {
                router.HandleTranscriptUpdated(
                    null,
                    Turn(TranscriptSpeaker.Caller, $"queued {index}", TranscriptStatus.Final));
                router.HandleActivityChanged(
                    null,
                    new CallerActivityChange(
                        CallerActivityState.Idle,
                        CallerActivityState.Speaking,
                        DateTimeOffset.UtcNow,
                        "queued"));
            }))
            .Append(Task.Run(() => router.HandleCallStateChanged(
                null,
                new CallStateChange(
                    CallSessionState.Connected,
                    CallSessionState.Ended,
                    DateTimeOffset.UtcNow,
                    "terminal"))))
            .ToArray();

        await Task.WhenAll(producerTasks);

        // Retirement is atomic with respect to the gate. Even worker events already posted to
        // the UI queue must recheck it in their UI handlers before mutating reducer/presenter.
        router.Retire();
        synchronizationContext.Drain();

        Assert.Equal(CallSessionState.Dialing, controller.State.CallState);
        Assert.Null(controller.State.Activity);
        Assert.Empty(presenter.Lines);
        Assert.Empty(diagnostics);
        Assert.Equal(0, terminalNotifications);
        Assert.False(gate.IsCurrent(generation));
    }

    [Fact]
    public async Task WorkerEvents_MutateOnlyWhenTheCapturedUiContextDrains()
    {
        var (controller, _, gate, _, _, generation) = CreateRouter();
        var synchronizationContext = new QueuedSynchronizationContext();
        var router = new CallEventRouter(
            controller,
            new TranscriptPresenter(),
            gate,
            generation,
            _ => { },
            new SerializedUiEventDispatcher(synchronizationContext));
        var mutationThreadId = 0;
        controller.StateChanged += (_, _) => mutationThreadId = Environment.CurrentManagedThreadId;

        await Task.Run(() => router.HandleActivityChanged(
            null,
            new CallerActivityChange(
                CallerActivityState.Idle,
                CallerActivityState.Listening,
                DateTimeOffset.UtcNow,
                "worker")));

        Assert.Null(controller.State.Activity);
        synchronizationContext.Drain();

        Assert.Equal(CallerActivityState.Listening, controller.State.Activity);
        Assert.Equal(Environment.CurrentManagedThreadId, mutationThreadId);
    }

    [Fact]
    public async Task Retirement_IsAtomicWithAnInFlightSerializedUiEvent()
    {
        var (controller, _, gate, _, _, generation) = CreateRouter();
        var synchronizationContext = new QueuedSynchronizationContext();
        using var diagnosticEntered = new ManualResetEventSlim();
        using var releaseDiagnostic = new ManualResetEventSlim();
        var router = new CallEventRouter(
            controller,
            new TranscriptPresenter(),
            gate,
            generation,
            _ =>
            {
                diagnosticEntered.Set();
                releaseDiagnostic.Wait(TimeSpan.FromSeconds(2));
            },
            new SerializedUiEventDispatcher(synchronizationContext));

        router.HandleAudioMonitorFaulted(null, new AudioMonitorFault("write", "safe"));
        var drain = Task.Run(synchronizationContext.Drain);
        Assert.True(diagnosticEntered.Wait(TimeSpan.FromSeconds(2)));

        var retirement = Task.Run(router.Retire);
        await Task.Delay(50);
        Assert.False(retirement.IsCompleted);

        releaseDiagnostic.Set();
        await Task.WhenAll(drain, retirement);

        Assert.False(gate.IsCurrent(generation));
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly object _sync = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = [];

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_sync)
            {
                _pending.Enqueue((callback, state));
            }
        }

        public void Drain()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) next;
                lock (_sync)
                {
                    if (_pending.Count == 0)
                    {
                        return;
                    }

                    next = _pending.Dequeue();
                }

                next.Callback(next.State);
            }
        }
    }
}
