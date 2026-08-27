using ServiceDeskCallSimulator.Calls;
using ServiceDeskCallSimulator.Conversation;
using ServiceDeskCallSimulator.Monitoring;

namespace ServiceDeskCallSimulator.UI;

/// <summary>
/// Routes one active call's worker-thread events (call state, transcript, activity, and local
/// audio monitor faults) into the WinForms-independent <see cref="SimulatorController"/> and
/// <see cref="TranscriptPresenter"/>. Every mutation is serialized onto the captured UI
/// dispatcher and then generation-filtered again on that UI handler. Extracted from MainForm
/// specifically so this event-routing policy — including "a monitor fault never ends the call",
/// and "the transcript is cleared only on the first event of a new call" — is unit testable
/// without any WinForms control. Handler methods use the standard <c>EventHandler&lt;T&gt;</c>
/// shape so MainForm can subscribe/unsubscribe with plain method-group references.
/// </summary>
public sealed class CallEventRouter
{
    private readonly SimulatorController _controller;
    private readonly TranscriptPresenter _transcriptPresenter;
    private readonly CallGenerationGate _gate;
    private readonly Action<string> _appendDiagnostics;
    private readonly IUiEventDispatcher _uiDispatcher;
    private readonly object _eventGate = new();
    private bool _transcriptClearedForThisCall;
    private int _endedNotified;
    private int _retired;

    public CallEventRouter(
        SimulatorController controller,
        TranscriptPresenter transcriptPresenter,
        CallGenerationGate gate,
        long generation,
        Action<string> appendDiagnostics,
        IUiEventDispatcher uiDispatcher)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _transcriptPresenter = transcriptPresenter ?? throw new ArgumentNullException(nameof(transcriptPresenter));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        Generation = generation;
        _appendDiagnostics = appendDiagnostics ?? throw new ArgumentNullException(nameof(appendDiagnostics));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
    }

    /// <summary>
    /// Gets the call generation this router was created for.
    /// </summary>
    public long Generation { get; }

    /// <summary>
    /// Raised once, on the call state transitioning to Ended or Faulted, so the owner can run
    /// its (async, disposal-heavy) finalize sequence. Never raised for a stale generation.
    /// </summary>
    public event Action<CallStateChange>? CallEnded;

    public void HandleCallStateChanged(object? sender, CallStateChange change)
    {
        PostIfCurrent(() =>
        {
            _controller.OnCallStateChanged(change.CurrentState);
            if (change.CurrentState is CallSessionState.Ended or CallSessionState.Faulted
                && Interlocked.Exchange(ref _endedNotified, 1) == 0)
            {
                CallEnded?.Invoke(change);
            }
        });
    }

    public void HandleTranscriptUpdated(object? sender, TranscriptTurn turn)
    {
        PostIfCurrent(() =>
        {
            if (!_transcriptClearedForThisCall)
            {
                _transcriptPresenter.Clear();
                _transcriptClearedForThisCall = true;
            }

            _transcriptPresenter.Apply(turn);
            _controller.SetHasTranscript(true);
        });
    }

    public void HandleActivityChanged(object? sender, CallerActivityChange change)
    {
        PostIfCurrent(() => _controller.OnActivityChanged(change.CurrentState));
    }

    /// <summary>
    /// A local waveOut/monitor failure is reported to diagnostics only. It never ends the call,
    /// changes call/activity state, or mutes ACS outbound audio.
    /// </summary>
    public void HandleAudioMonitorFaulted(object? sender, AudioMonitorFault fault)
    {
        PostIfCurrent(() => _appendDiagnostics($"Local audio monitor ({fault.Operation}): {fault.Message}"));
    }

    /// <summary>
    /// Atomically retires this generation before event unsubscription or resource disposal.
    /// Queued UI work checks this value again before it can mutate the reducer or presenter.
    /// </summary>
    public void Retire()
    {
        lock (_eventGate)
        {
            if (Interlocked.Exchange(ref _retired, 1) == 0)
            {
                _gate.Retire(Generation);
            }
        }
    }

    private void PostIfCurrent(Action action)
    {
        _uiDispatcher.Post(() =>
        {
            // Make the gate recheck and state/presenter mutation one critical section with
            // Retire(). A teardown that wins this lock rejects this queued action; otherwise
            // the action completes before teardown can retire the generation.
            lock (_eventGate)
            {
                if (Volatile.Read(ref _retired) != 0 || !_gate.IsCurrent(Generation))
                {
                    return;
                }

                action();
            }
        });
    }
}
