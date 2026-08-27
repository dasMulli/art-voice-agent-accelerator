using ServiceDeskCallSimulator.Calls;
using ServiceDeskCallSimulator.Conversation;
using ServiceDeskCallSimulator.PhoneNumbers;
using ServiceDeskCallSimulator.Presets;
using ServiceDeskCallSimulator.Validation;

namespace ServiceDeskCallSimulator.UI;

/// <summary>
/// A small, WinForms-independent view-state reducer/controller. It owns the immutable
/// <see cref="SimulatorViewState"/>, decides button/control enablement and status text from
/// initialization, validation, and call/activity state, and guards against duplicate
/// concurrent operations. MainForm applies the emitted state to its controls; it never
/// derives enablement itself.
/// </summary>
public sealed class SimulatorController
{
    private SimulatorViewState _state = new();
    private bool _hangUpRequested;

    /// <summary>
    /// Gets the current immutable view state.
    /// </summary>
    public SimulatorViewState State => _state;

    /// <summary>
    /// Raised synchronously whenever the state changes.
    /// </summary>
    public event EventHandler<SimulatorViewState>? StateChanged;

    // ---- Initialization lifecycle -----------------------------------------------------

    public void BeginInitialization()
    {
        _hangUpRequested = false;
        // Preserve IsRetryingInitialization: RunRetryAsync calls BeginRetry() before this
        // method reruns the whole sequence, so the in-progress-retry flag (and its
        // double-retry guard) must survive this otherwise-full state reset.
        Update(s => new SimulatorViewState
        {
            Phase = AppPhase.Initializing,
            IsRetryingInitialization = s.IsRetryingInitialization,
        });
    }

    public void ReportStageStarted(InitializationStage stage) =>
        Update(s => s with { Checklist = WithStatus(s.Checklist, stage, InitializationStageStatus.InProgress) });

    public void ReportStageCompleted(InitializationStage stage) =>
        Update(s => s with { Checklist = WithStatus(s.Checklist, stage, InitializationStageStatus.Done) });

    public void ReportStageFailed(InitializationStage stage, string safeError) =>
        Update(s => s with
        {
            Checklist = WithStatus(s.Checklist, stage, InitializationStageStatus.Failed),
            Phase = AppPhase.Error,
            InitializationError = safeError,
        });

    /// <summary>
    /// Marks the current Dev Tunnel as failed while its exact active call and session are
    /// retired. The error is visible immediately, but retry remains disabled until
    /// <see cref="CompleteTunnelFailureCleanup"/> runs.
    /// </summary>
    public void BeginTunnelFailureCleanup(string safeError) =>
        Update(s => s with
        {
            Checklist = WithStatus(
                s.Checklist,
                InitializationStage.DevTunnel,
                InitializationStageStatus.Failed),
            Phase = AppPhase.Error,
            InitializationError = safeError,
            IsTunnelFailureCleanupInProgress = true,
        });

    /// <summary>
    /// Allows the initialization error state to offer Retry after failed-session cleanup ends.
    /// </summary>
    public void CompleteTunnelFailureCleanup() =>
        Update(s => s with { IsTunnelFailureCleanupInProgress = false });

    public void BeginSignIn() =>
        Update(s => s with { Phase = AppPhase.SignInRequired });

    public void EndSignIn() =>
        Update(s => s with { Phase = s.Phase == AppPhase.SignInRequired ? AppPhase.Initializing : s.Phase });

    /// <summary>
    /// Attempts to begin a retry. Returns false when a retry is already running so callers
    /// never start a second concurrent retry.
    /// </summary>
    public bool BeginRetry()
    {
        if (_state.IsRetryingInitialization
            || _state.IsTunnelFailureCleanupInProgress)
        {
            return false;
        }

        Update(s => s with { IsRetryingInitialization = true });
        return true;
    }

    public void EndRetry() => Update(s => s with { IsRetryingInitialization = false });

    public void CompleteInitialization(
        PhoneNumberSelectionResult discovery,
        string destination,
        IReadOnlyList<string> presetNames,
        string? publicCallbackHost,
        string? selectedModel)
    {
        ArgumentNullException.ThrowIfNull(discovery);

        Update(s => s with
        {
            Phase = AppPhase.Ready,
            CallerIds = discovery.OutboundNumbers,
            SelectedCallerId = discovery.SelectedPhoneNumber,
            Destination = destination,
            PresetNames = presetNames,
            SelectedPresetIndex = presetNames.Count > 0 ? 0 : -1,
            PublicCallbackHost = publicCallbackHost,
            SelectedModel = selectedModel,
            InitializationError = null,
            IsTunnelFailureCleanupInProgress = false,
        });
    }

    // ---- Routing ------------------------------------------------------------------------

    public bool BeginRefreshNumbers()
    {
        if (_state.IsRefreshingNumbers)
        {
            return false;
        }

        Update(s => s with { IsRefreshingNumbers = true, RefreshError = null });
        return true;
    }

    /// <summary>
    /// Applies a refreshed discovery result. The current selection is preserved only when
    /// still present among outbound-capable numbers; otherwise the preferred-only rule
    /// already applied by <see cref="PhoneNumberSelector"/> is used.
    /// </summary>
    public void CompleteRefreshNumbers(PhoneNumberSelectionResult discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);

        Update(s =>
        {
            var currentStillPresent = s.SelectedCallerId is { } current
                && discovery.OutboundNumbers.Contains(current, StringComparer.Ordinal);
            var selected = currentStillPresent ? s.SelectedCallerId : discovery.SelectedPhoneNumber;
            return s with
            {
                IsRefreshingNumbers = false,
                CallerIds = discovery.OutboundNumbers,
                SelectedCallerId = selected,
                RefreshError = null,
            };
        });
    }

    public void FailRefreshNumbers(string safeError) =>
        Update(s => s with { IsRefreshingNumbers = false, RefreshError = safeError });

    public void SelectCallerId(string? callerId) =>
        Update(s => s with { SelectedCallerId = callerId });

    public void SetDestination(string destination) =>
        Update(s => s with { Destination = destination });

    // ---- Caller script presets ------------------------------------------------------------

    /// <summary>
    /// Requests a preset switch. When the current draft is unchanged from the currently
    /// selected preset's baseline the switch applies immediately and true is returned.
    /// Otherwise the switch is held pending confirmation and false is returned.
    /// </summary>
    public bool RequestPresetSelection(int index, CallerScriptDraft candidateDraft, bool isDirty)
    {
        ArgumentNullException.ThrowIfNull(candidateDraft);

        if (!isDirty)
        {
            ApplyPresetSelection(index, candidateDraft);
            return true;
        }

        Update(s => s with { PendingPresetIndex = index, PendingPresetDraft = candidateDraft });
        return false;
    }

    /// <summary>
    /// Resolves a pending preset switch. Declining preserves the current selection and
    /// edits without raising a further selection event.
    /// </summary>
    public void ConfirmPendingPresetSelection(bool accept)
    {
        if (!_state.PendingPresetIndex.HasValue)
        {
            return;
        }

        if (accept)
        {
            ApplyPresetSelection(_state.PendingPresetIndex.Value, _state.PendingPresetDraft!);
        }
        else
        {
            Update(s => s with { PendingPresetIndex = null, PendingPresetDraft = null });
        }
    }

    private void ApplyPresetSelection(int index, CallerScriptDraft draft) =>
        Update(s => s with
        {
            SelectedPresetIndex = index,
            Draft = draft,
            PendingPresetIndex = null,
            PendingPresetDraft = null,
        });

    public void ResetDraftToPreset(CallerScriptDraft presetDraft) =>
        Update(s => s with { Draft = presetDraft });

    public void UpdateDraft(CallerScriptDraft draft) =>
        Update(s => s with { Draft = draft });

    // ---- Call lifecycle -------------------------------------------------------------------

    /// <summary>
    /// Attempts to start dialing. Returns false when the current state does not allow a
    /// call, including when a call is already dialing (double-start prevention).
    /// </summary>
    public bool BeginDial()
    {
        if (!IsCallEnabled(_state))
        {
            return false;
        }

        _hangUpRequested = false;
        Update(s => s with
        {
            Phase = AppPhase.Dialing,
            CallState = CallSessionState.Dialing,
            IsDialing = true,
            Activity = null,
            Elapsed = TimeSpan.Zero,
            CallStatusMessage = null,
        });
        return true;
    }

    public void OnCallStateChanged(CallSessionState callState) =>
        Update(s => s with
        {
            CallState = callState,
            Phase = callState switch
            {
                CallSessionState.Dialing => AppPhase.Dialing,
                CallSessionState.Connected => AppPhase.Connected,
                CallSessionState.Ending => AppPhase.Ending,
                _ => s.Phase,
            },
        });

    public void OnActivityChanged(CallerActivityState activity) =>
        Update(s => s with { Activity = activity });

    public void Tick(TimeSpan elapsed) =>
        Update(s => s with { Elapsed = elapsed });

    /// <summary>
    /// Attempts to begin hang-up. Returns false when hang-up was already requested for the
    /// active call so only one hang-up operation is ever started (the underlying session's
    /// hang-up itself remains independently idempotent).
    /// </summary>
    public bool BeginHangUp()
    {
        if (_hangUpRequested || !_state.IsCallInProgress)
        {
            return false;
        }

        _hangUpRequested = true;
        Update(s => s with { Phase = AppPhase.Ending, CallState = CallSessionState.Ending });
        return true;
    }

    /// <summary>
    /// Returns the setup pane and controls to Ready without recreating the tunnel. Existing
    /// transcript content is intentionally left untouched by this reducer.
    /// </summary>
    public void CompleteCall(string safeEndReason)
    {
        _hangUpRequested = false;
        Update(s => s with
        {
            Phase = s.Phase == AppPhase.Error ? AppPhase.Error : AppPhase.Ready,
            CallState = null,
            Activity = null,
            IsDialing = false,
            CallStatusMessage = safeEndReason,
        });
    }

    public void SetMuted(bool muted) =>
        Update(s => s with { IsMutedLocally = muted });

    public void SetHasTranscript(bool hasTranscript) =>
        Update(s => s with { HasTranscript = hasTranscript });

    // ---- Pure enablement/validation rules ---------------------------------------------

    public static bool IsCallerIdValid(SimulatorViewState state) =>
        state.SelectedCallerId is { } value && E164PhoneNumber.IsValid(value);

    public static bool IsDestinationValid(SimulatorViewState state) =>
        E164PhoneNumber.IsValid(state.Destination);

    public static bool IsCallbackNumberValid(SimulatorViewState state) =>
        state.Draft is { } draft && E164PhoneNumber.IsValid(draft.CallbackNumber);

    public static bool HasSelectedPreset(SimulatorViewState state) =>
        state.SelectedPresetIndex >= 0 && state.Draft is not null;

    public static bool IsCallEnabled(SimulatorViewState state) =>
        state.Phase == AppPhase.Ready
        && !state.IsDialing
        && !state.IsRefreshingNumbers
        && !state.HasPendingPresetConfirmation
        && IsCallerIdValid(state)
        && IsDestinationValid(state)
        && IsCallbackNumberValid(state)
        && HasSelectedPreset(state);

    public static bool IsHangUpEnabled(SimulatorViewState state) => state.IsCallInProgress;

    public static bool IsRefreshEnabled(SimulatorViewState state) =>
        state.Phase == AppPhase.Ready && !state.IsRefreshingNumbers && !state.IsDialing;

    public static bool IsRetryEnabled(SimulatorViewState state) =>
        state.Phase == AppPhase.Error
        && !state.IsRetryingInitialization
        && !state.IsTunnelFailureCleanupInProgress;

    public static bool IsClearTranscriptEnabled(SimulatorViewState state) => !state.IsCallInProgress;

    public static bool IsSetupLocked(SimulatorViewState state) => state.IsSetupLocked;

    /// <summary>
    /// Produces a short, safe explanation for why the Call button is currently disabled, or
    /// an empty string when it is enabled.
    /// </summary>
    public static string DescribeCallDisabledReason(SimulatorViewState state)
    {
        if (IsCallEnabled(state))
        {
            return string.Empty;
        }

        if (state.Phase != AppPhase.Ready)
        {
            if (state.IsTunnelFailureCleanupInProgress)
            {
                return "Cleaning up the failed Dev Tunnel session.";
            }

            return "Waiting for initialization to finish.";
        }

        if (state.HasPendingPresetConfirmation)
        {
            return "Resolve the pending preset change first.";
        }

        if (state.IsRefreshingNumbers)
        {
            return "Waiting for number discovery to refresh.";
        }

        if (!HasSelectedPreset(state))
        {
            return "Select a caller script preset first.";
        }

        if (!IsCallerIdValid(state))
        {
            return "Select a valid caller ID.";
        }

        if (!IsDestinationValid(state))
        {
            return "Enter a valid destination E.164 number.";
        }

        if (!IsCallbackNumberValid(state))
        {
            return "The script's callback number must be a valid E.164 number.";
        }

        return "Call is currently unavailable.";
    }

    /// <summary>
    /// Produces accessible, human-readable status text for the current phase.
    /// </summary>
    public static string DescribePhase(AppPhase phase) => phase switch
    {
        AppPhase.Initializing => "Initializing",
        AppPhase.SignInRequired => "Sign-in required",
        AppPhase.Ready => "Ready",
        AppPhase.Dialing => "Dialing",
        AppPhase.Connected => "Connected",
        AppPhase.Ending => "Ending",
        AppPhase.Error => "Error",
        _ => phase.ToString(),
    };

    private static IReadOnlyList<InitializationChecklistItem> WithStatus(
        IReadOnlyList<InitializationChecklistItem> checklist,
        InitializationStage stage,
        InitializationStageStatus status)
    {
        return checklist
            .Select(item => item.Stage == stage ? item with { Status = status } : item)
            .ToArray();
    }

    private void Update(Func<SimulatorViewState, SimulatorViewState> mutate)
    {
        _state = mutate(_state);
        StateChanged?.Invoke(this, _state);
    }
}
