using ServiceDeskCallSimulator.Calls;
using ServiceDeskCallSimulator.Conversation;
using ServiceDeskCallSimulator.Presets;

namespace ServiceDeskCallSimulator.UI;

/// <summary>
/// The top-level, mutually exclusive application phase shown in the status header.
/// </summary>
public enum AppPhase
{
    Initializing,
    SignInRequired,
    Ready,
    Dialing,
    Connected,
    Ending,
    Error,
}

/// <summary>
/// One discrete startup dependency tracked by the initialization checklist.
/// </summary>
public enum InitializationStage
{
    AzureAuthentication,
    NumberDiscovery,
    CallbackHost,
    DevTunnel,
}

/// <summary>
/// The progress of one initialization checklist row.
/// </summary>
public enum InitializationStageStatus
{
    Pending,
    InProgress,
    Done,
    Failed,
}

/// <summary>
/// One immutable initialization checklist row.
/// </summary>
public sealed record InitializationChecklistItem(InitializationStage Stage, InitializationStageStatus Status);

/// <summary>
/// The complete, immutable view state used to drive the WinForms UI. Produced only by
/// <see cref="SimulatorController"/> so its transitions can be unit tested without any
/// WinForms control.
/// </summary>
public sealed record SimulatorViewState
{
    private static readonly IReadOnlyList<InitializationChecklistItem> InitialChecklist =
    [
        new(InitializationStage.AzureAuthentication, InitializationStageStatus.Pending),
        new(InitializationStage.NumberDiscovery, InitializationStageStatus.Pending),
        new(InitializationStage.CallbackHost, InitializationStageStatus.Pending),
        new(InitializationStage.DevTunnel, InitializationStageStatus.Pending),
    ];

    public AppPhase Phase { get; init; } = AppPhase.Initializing;

    public IReadOnlyList<InitializationChecklistItem> Checklist { get; init; } = InitialChecklist;

    public string? InitializationError { get; init; }

    public bool IsRetryingInitialization { get; init; }

    /// <summary>
    /// Gets whether a failed current Dev Tunnel session is still retiring its call and owned
    /// resources. Retry must remain unavailable until this ordered cleanup is complete.
    /// </summary>
    public bool IsTunnelFailureCleanupInProgress { get; init; }

    public string? PublicCallbackHost { get; init; }

    public string? SelectedModel { get; init; }

    public IReadOnlyList<string> CallerIds { get; init; } = [];

    public string? SelectedCallerId { get; init; }

    public bool IsRefreshingNumbers { get; init; }

    public string? RefreshError { get; init; }

    public string Destination { get; init; } = string.Empty;

    public IReadOnlyList<string> PresetNames { get; init; } = [];

    public int SelectedPresetIndex { get; init; } = -1;

    public CallerScriptDraft? Draft { get; init; }

    public int? PendingPresetIndex { get; init; }

    public CallerScriptDraft? PendingPresetDraft { get; init; }

    public CallSessionState? CallState { get; init; }

    public CallerActivityState? Activity { get; init; }

    public TimeSpan Elapsed { get; init; }

    public bool IsDialing { get; init; }

    public string? CallStatusMessage { get; init; }

    public bool IsMutedLocally { get; init; }

    public bool HasTranscript { get; init; }

    /// <summary>
    /// Gets a value indicating whether every checklist stage has completed successfully.
    /// </summary>
    public bool IsFullyInitialized => Checklist.All(item => item.Status == InitializationStageStatus.Done);

    /// <summary>
    /// Gets a value indicating whether an outbound call is dialing, connected, or ending.
    /// </summary>
    public bool IsCallInProgress => CallState is CallSessionState.Dialing or CallSessionState.Connected or CallSessionState.Ending;

    /// <summary>
    /// Gets a value indicating whether the routing/script setup controls must be locked.
    /// </summary>
    public bool IsSetupLocked => IsCallInProgress;

    /// <summary>
    /// Gets a value indicating whether a preset switch is awaiting user confirmation.
    /// </summary>
    public bool HasPendingPresetConfirmation => PendingPresetIndex.HasValue;
}
