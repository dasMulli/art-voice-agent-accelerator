using ServiceDeskCallSimulator.Calls;
using ServiceDeskCallSimulator.Conversation;
using ServiceDeskCallSimulator.PhoneNumbers;
using ServiceDeskCallSimulator.Presets;
using ServiceDeskCallSimulator.UI;

namespace ServiceDeskCallSimulator.Tests;

public sealed class SimulatorControllerTests
{
    private static CallerScriptDraft Draft(string callbackNumber = "+14155550101") => new()
    {
        Name = "[EN] Printer not working",
        Locale = "en-US",
        Voice = "en-US-JennyNeural",
        OpeningLine = "Hello.",
        Identity = "Maya",
        Background = "Background",
        Reason = "Reason",
        Urgency = "High",
        CallbackNumber = callbackNumber,
        AdditionalDetails = "Details",
    };

    private static SimulatorController ReadyController(
        string? selectedCallerId = "+43800223359",
        string destination = "+33801150311")
    {
        var controller = new SimulatorController();
        controller.BeginInitialization();
        controller.ReportStageStarted(InitializationStage.AzureAuthentication);
        controller.ReportStageCompleted(InitializationStage.AzureAuthentication);
        controller.ReportStageStarted(InitializationStage.NumberDiscovery);
        controller.ReportStageCompleted(InitializationStage.NumberDiscovery);
        controller.ReportStageStarted(InitializationStage.CallbackHost);
        controller.ReportStageCompleted(InitializationStage.CallbackHost);
        controller.ReportStageStarted(InitializationStage.DevTunnel);
        controller.ReportStageCompleted(InitializationStage.DevTunnel);

        var discovery = new PhoneNumberSelectionResult(["+43800223359", "+15550000000"], selectedCallerId);
        controller.CompleteInitialization(discovery, destination, ["[EN] Printer not working"], "abc123.devtunnels.ms", "gpt-5.6-luna");
        controller.RequestPresetSelection(0, Draft(), isDirty: false);
        return controller;
    }

    // ---- View-state matrix ----------------------------------------------------------------

    [Fact]
    public void DescribePhase_CoversEveryPhaseWithDistinctText()
    {
        var texts = Enum.GetValues<AppPhase>().Select(SimulatorController.DescribePhase).ToArray();
        Assert.Equal(texts.Length, texts.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("Initializing", texts);
        Assert.Contains("Sign-in required", texts);
        Assert.Contains("Ready", texts);
        Assert.Contains("Dialing", texts);
        Assert.Contains("Connected", texts);
        Assert.Contains("Ending", texts);
        Assert.Contains("Error", texts);
    }

    [Fact]
    public void InitialState_IsInitializingWithAllChecklistItemsPending()
    {
        var controller = new SimulatorController();
        Assert.Equal(AppPhase.Initializing, controller.State.Phase);
        Assert.All(controller.State.Checklist, item => Assert.Equal(InitializationStageStatus.Pending, item.Status));
        Assert.False(SimulatorController.IsCallEnabled(controller.State));
    }

    [Fact]
    public void SignIn_TransitionsToSignInRequiredAndBackToInitializing()
    {
        var controller = new SimulatorController();
        controller.BeginInitialization();
        controller.BeginSignIn();
        Assert.Equal(AppPhase.SignInRequired, controller.State.Phase);

        controller.EndSignIn();
        Assert.Equal(AppPhase.Initializing, controller.State.Phase);
    }

    [Fact]
    public void ReportStageFailed_SetsErrorPhaseAndDisablesCall()
    {
        var controller = new SimulatorController();
        controller.BeginInitialization();
        controller.ReportStageStarted(InitializationStage.DevTunnel);
        controller.ReportStageFailed(InitializationStage.DevTunnel, "Dev Tunnels CLI was not found.");

        Assert.Equal(AppPhase.Error, controller.State.Phase);
        Assert.Equal("Dev Tunnels CLI was not found.", controller.State.InitializationError);
        Assert.False(SimulatorController.IsCallEnabled(controller.State));
        Assert.True(SimulatorController.IsRetryEnabled(controller.State));
    }

    [Fact]
    public void CompleteInitialization_TransitionsToReady()
    {
        var controller = ReadyController();
        Assert.Equal(AppPhase.Ready, controller.State.Phase);
        Assert.True(controller.State.IsFullyInitialized);
    }

    [Fact]
    public void DialingConnectedEnding_ReflectPhaseFromCallState()
    {
        var controller = ReadyController();
        Assert.True(controller.BeginDial());
        Assert.Equal(AppPhase.Dialing, controller.State.Phase);

        controller.OnCallStateChanged(CallSessionState.Connected);
        Assert.Equal(AppPhase.Connected, controller.State.Phase);

        Assert.True(controller.BeginHangUp());
        Assert.Equal(AppPhase.Ending, controller.State.Phase);
    }

    [Fact]
    public void InvalidRoutingValues_DisableCall()
    {
        var controller = ReadyController(selectedCallerId: null);
        Assert.False(SimulatorController.IsCallEnabled(controller.State));

        controller.SetDestination("not-a-number");
        Assert.False(SimulatorController.IsDestinationValid(controller.State));
    }

    // ---- Preferred caller ID and refresh preservation --------------------------------------

    [Fact]
    public void CompleteInitialization_SelectsPreferredCallerIdOnlyWhenDiscoverySelectedIt()
    {
        var controller = new SimulatorController();
        var discoveryWithPreferred = new PhoneNumberSelectionResult(["+43800223359", "+15550000000"], "+43800223359");
        controller.CompleteInitialization(discoveryWithPreferred, "+33801150311", [], null, null);
        Assert.Equal("+43800223359", controller.State.SelectedCallerId);

        var controllerWithoutPreferred = new SimulatorController();
        var discoveryWithoutPreferred = new PhoneNumberSelectionResult(["+15550000000"], null);
        controllerWithoutPreferred.CompleteInitialization(discoveryWithoutPreferred, "+33801150311", [], null, null);
        Assert.Null(controllerWithoutPreferred.State.SelectedCallerId);
    }

    [Fact]
    public void CompleteRefreshNumbers_PreservesCurrentSelectionWhenStillPresent()
    {
        var controller = ReadyController(selectedCallerId: "+15550000000");
        Assert.True(controller.BeginRefreshNumbers());

        var refreshed = new PhoneNumberSelectionResult(["+43800223359", "+15550000000"], "+43800223359");
        controller.CompleteRefreshNumbers(refreshed);

        Assert.Equal("+15550000000", controller.State.SelectedCallerId);
        Assert.False(controller.State.IsRefreshingNumbers);
    }

    [Fact]
    public void CompleteRefreshNumbers_AppliesPreferredOnlyRuleWhenCurrentSelectionIsGone()
    {
        var controller = ReadyController(selectedCallerId: "+19998887777");
        // The current selection is not present in the refreshed set below.
        controller.BeginRefreshNumbers();

        var refreshed = new PhoneNumberSelectionResult(["+43800223359"], "+43800223359");
        controller.CompleteRefreshNumbers(refreshed);

        Assert.Equal("+43800223359", controller.State.SelectedCallerId);
    }

    [Fact]
    public void CompleteRefreshNumbers_LeavesCallerIdBlankWhenNeitherCurrentNorPreferredArePresent()
    {
        var controller = ReadyController(selectedCallerId: "+19998887777");
        controller.BeginRefreshNumbers();

        var refreshed = new PhoneNumberSelectionResult(["+15550000000"], null);
        controller.CompleteRefreshNumbers(refreshed);

        Assert.Null(controller.State.SelectedCallerId);
    }

    [Fact]
    public void FailRefreshNumbers_ExposesInlineFailureWithoutLeavingReadyState()
    {
        var controller = ReadyController();
        controller.BeginRefreshNumbers();

        controller.FailRefreshNumbers("Number discovery is temporarily unavailable.");

        Assert.Equal(AppPhase.Ready, controller.State.Phase);
        Assert.False(controller.State.IsRefreshingNumbers);
        Assert.Equal("Number discovery is temporarily unavailable.", controller.State.RefreshError);
        Assert.True(SimulatorController.IsCallEnabled(controller.State));
    }

    // ---- Call enablement --------------------------------------------------------------------

    [Fact]
    public void IsCallEnabled_RequiresReadyPhaseValidRoutingAndSelectedPreset()
    {
        var controller = ReadyController();
        Assert.True(SimulatorController.IsCallEnabled(controller.State));
    }

    [Fact]
    public void IsCallEnabled_FalseWhenRefreshingNumbers()
    {
        var controller = ReadyController();
        controller.BeginRefreshNumbers();
        Assert.False(SimulatorController.IsCallEnabled(controller.State));
    }

    [Fact]
    public void IsCallEnabled_FalseWhenCallbackNumberInScriptIsInvalid()
    {
        var controller = ReadyController();
        controller.UpdateDraft(Draft(callbackNumber: "not-e164"));
        Assert.False(SimulatorController.IsCallEnabled(controller.State));
    }

    [Fact]
    public void IsCallEnabled_FalseWhenNoPresetSelected()
    {
        var controller = new SimulatorController();
        var discovery = new PhoneNumberSelectionResult(["+43800223359"], "+43800223359");
        controller.CompleteInitialization(discovery, "+33801150311", [], null, null);
        Assert.False(SimulatorController.IsCallEnabled(controller.State));
        Assert.Contains("preset", SimulatorController.DescribeCallDisabledReason(controller.State), StringComparison.OrdinalIgnoreCase);
    }

    // ---- Preset selection, dirty confirmation, and reset -----------------------------------

    [Fact]
    public void RequestPresetSelection_AppliesImmediatelyWhenNotDirty()
    {
        var controller = ReadyController();
        var applied = controller.RequestPresetSelection(1, Draft(), isDirty: false);
        Assert.True(applied);
        Assert.Equal(1, controller.State.SelectedPresetIndex);
        Assert.False(controller.State.HasPendingPresetConfirmation);
    }

    [Fact]
    public void RequestPresetSelection_HoldsPendingWhenDirtyUntilConfirmed()
    {
        var controller = ReadyController();
        var applied = controller.RequestPresetSelection(1, Draft(), isDirty: true);

        Assert.False(applied);
        Assert.True(controller.State.HasPendingPresetConfirmation);
        Assert.Equal(0, controller.State.SelectedPresetIndex); // unchanged until confirmed
    }

    [Fact]
    public void ConfirmPendingPresetSelection_Accept_AppliesTheNewPreset()
    {
        var controller = ReadyController();
        var newDraft = Draft(callbackNumber: "+14155550199");
        controller.RequestPresetSelection(1, newDraft, isDirty: true);

        controller.ConfirmPendingPresetSelection(accept: true);

        Assert.Equal(1, controller.State.SelectedPresetIndex);
        Assert.Same(newDraft, controller.State.Draft);
        Assert.False(controller.State.HasPendingPresetConfirmation);
    }

    [Fact]
    public void ConfirmPendingPresetSelection_Decline_PreservesCurrentSelectionAndEdits()
    {
        var controller = ReadyController();
        var editedDraft = Draft(callbackNumber: "+14155550188");
        controller.UpdateDraft(editedDraft);

        controller.RequestPresetSelection(1, Draft(callbackNumber: "+14155550199"), isDirty: true);
        controller.ConfirmPendingPresetSelection(accept: false);

        Assert.Equal(0, controller.State.SelectedPresetIndex);
        Assert.Same(editedDraft, controller.State.Draft);
        Assert.False(controller.State.HasPendingPresetConfirmation);
    }

    [Fact]
    public void ResetDraftToPreset_ReplacesEditBufferOnly()
    {
        var controller = ReadyController();
        controller.UpdateDraft(Draft(callbackNumber: "+14155550188"));

        var presetDraft = Draft();
        controller.ResetDraftToPreset(presetDraft);

        Assert.Same(presetDraft, controller.State.Draft);
        Assert.Equal(0, controller.State.SelectedPresetIndex);
    }

    // ---- Exactly nine presets, no independent language selector ----------------------------

    [Fact]
    public void CreateDefaultPresets_ReturnsExactlyNineEntriesWithNoLanguageSelector()
    {
        var settings = new Configuration.SimulatorSettings();
        var presets = CallerScriptPresetCatalog.CreateDefaultPresets(settings);

        Assert.Equal(9, presets.Count);
        Assert.Contains(presets, p => p.Name == "[EN] Printer not working");
        Assert.Contains(presets, p => p.Name == "[DE] Drucker funktioniert nicht");
        Assert.Contains(presets, p => p.Name == "[DE→PL] Netzwerkstörung / awaria sieci");

        // The preset itself carries locale/voice; CallerScriptDraft exposes them as read-only
        // fields alongside the preset choice rather than through any separate language field.
        Assert.All(presets, preset => Assert.False(string.IsNullOrWhiteSpace(preset.Locale)));
        Assert.All(presets, preset => Assert.False(string.IsNullOrWhiteSpace(preset.Voice)));
    }

    // ---- Lock/unlock over call lifecycle -----------------------------------------------------

    [Fact]
    public void SetupIsLocked_OnlyWhileDialingConnectedOrEnding()
    {
        var controller = ReadyController();
        Assert.False(controller.State.IsSetupLocked);

        controller.BeginDial();
        Assert.True(controller.State.IsSetupLocked);

        controller.OnCallStateChanged(CallSessionState.Connected);
        Assert.True(controller.State.IsSetupLocked);

        controller.BeginHangUp();
        Assert.True(controller.State.IsSetupLocked);

        controller.CompleteCall("Manual hang-up requested.");
        Assert.False(controller.State.IsSetupLocked);
    }

    // ---- Double Call/Refresh/Retry prevention ------------------------------------------------

    [Fact]
    public void BeginDial_SecondCallWhileDialingIsRejected()
    {
        var controller = ReadyController();
        Assert.True(controller.BeginDial());
        Assert.False(controller.BeginDial());
    }

    [Fact]
    public void BeginRefreshNumbers_SecondCallWhileRefreshingIsRejected()
    {
        var controller = ReadyController();
        Assert.True(controller.BeginRefreshNumbers());
        Assert.False(controller.BeginRefreshNumbers());
    }

    [Fact]
    public void BeginRetry_SecondCallWhileRetryingIsRejected()
    {
        var controller = new SimulatorController();
        controller.ReportStageFailed(InitializationStage.DevTunnel, "boom");
        Assert.True(controller.BeginRetry());
        Assert.False(controller.BeginRetry());
        controller.EndRetry();
        Assert.True(controller.BeginRetry());
    }

    [Fact]
    public void BeginRetry_FlagSurvivesTheFullReinitializationSequenceItTriggers()
    {
        // Mirrors MainForm.RunRetryAsync: BeginRetry() is called once by the Retry button,
        // then BeginInitialization() reruns the whole startup sequence. The in-progress-retry
        // guard must not be lost by that reset, or a second concurrent retry could slip through.
        var controller = new SimulatorController();
        controller.ReportStageFailed(InitializationStage.DevTunnel, "boom");

        Assert.True(controller.BeginRetry());
        controller.BeginInitialization();

        Assert.False(controller.BeginRetry());
        Assert.True(controller.State.IsRetryingInitialization);

        controller.EndRetry();
        Assert.True(controller.BeginRetry());
    }

    [Fact]
    public void TunnelFailureCleanup_DisablesRetryUntilTheCurrentSessionHasFinishedCleanup()
    {
        var controller = ReadyController();
        controller.BeginDial();
        controller.OnCallStateChanged(CallSessionState.Connected);

        controller.BeginTunnelFailureCleanup("The Dev Tunnel stopped unexpectedly.");
        controller.CompleteCall("The Dev Tunnel stopped unexpectedly.");

        Assert.Equal(AppPhase.Error, controller.State.Phase);
        Assert.False(SimulatorController.IsRetryEnabled(controller.State));
        Assert.False(controller.BeginRetry());

        controller.CompleteTunnelFailureCleanup();

        Assert.True(SimulatorController.IsRetryEnabled(controller.State));
        Assert.True(controller.BeginRetry());
    }

    // ---- Idempotent Hang Up -------------------------------------------------------------------

    [Fact]
    public void BeginHangUp_SecondRequestWhileEndingIsRejectedButRemainsInEndingPhase()
    {
        var controller = ReadyController();
        controller.BeginDial();
        controller.OnCallStateChanged(CallSessionState.Connected);

        Assert.True(controller.BeginHangUp());
        Assert.False(controller.BeginHangUp());
        Assert.Equal(AppPhase.Ending, controller.State.Phase);
        Assert.True(SimulatorController.IsHangUpEnabled(controller.State));
    }

    [Fact]
    public void BeginHangUp_NotAllowedWhenNoCallInProgress()
    {
        var controller = ReadyController();
        Assert.False(controller.BeginHangUp());
    }

    [Fact]
    public void CompleteCall_AllowsHangUpAgainOnTheNextCall()
    {
        var controller = ReadyController();
        controller.BeginDial();
        controller.OnCallStateChanged(CallSessionState.Connected);
        controller.BeginHangUp();
        controller.CompleteCall("Ended.");

        Assert.True(controller.BeginDial());
        controller.OnCallStateChanged(CallSessionState.Connected);
        Assert.True(controller.BeginHangUp());
    }

    // ---- Completion retains transcript-related flag and reuses tunnel (state-level) --------

    [Fact]
    public void CompleteCall_ReturnsToReadyWithoutClearingHasTranscriptFlag()
    {
        var controller = ReadyController();
        controller.BeginDial();
        controller.SetHasTranscript(true);
        controller.OnCallStateChanged(CallSessionState.Connected);
        controller.BeginHangUp();
        controller.CompleteCall("Manual hang-up requested.");

        Assert.Equal(AppPhase.Ready, controller.State.Phase);
        Assert.True(controller.State.HasTranscript);
        Assert.Null(controller.State.CallState);
    }

    [Fact]
    public void CompleteCall_DoesNotRequireOrTouchPublicCallbackHost()
    {
        var controller = ReadyController();
        var hostBefore = controller.State.PublicCallbackHost;
        controller.BeginDial();
        controller.OnCallStateChanged(CallSessionState.Faulted);
        controller.CompleteCall("Call faulted.");

        Assert.Equal(hostBefore, controller.State.PublicCallbackHost);
        Assert.Equal(AppPhase.Ready, controller.State.Phase);
    }
}
