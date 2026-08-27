using System.Diagnostics;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using ServiceDeskCallSimulator.Azure;
using ServiceDeskCallSimulator.Calls;
using ServiceDeskCallSimulator.Configuration;
using ServiceDeskCallSimulator.Conversation;
using ServiceDeskCallSimulator.DevTunnel;
using ServiceDeskCallSimulator.Callback;
using ServiceDeskCallSimulator.Monitoring;
using ServiceDeskCallSimulator.PhoneNumbers;
using ServiceDeskCallSimulator.Presets;
using ServiceDeskCallSimulator.Speech;

namespace ServiceDeskCallSimulator.UI;

/// <summary>
/// The production main window. Composes the accepted Task 1-4 services, shows immediately,
/// and initializes Azure/ACS/Kestrel/Dev Tunnel asynchronously from <see cref="OnShown"/> so
/// the UI thread is never blocked. All enablement/status decisions are delegated to
/// <see cref="SimulatorController"/>; this class only applies its emitted state to controls
/// and marshals worker events back to the UI thread.
/// </summary>
public partial class MainForm : Form
{
    private static readonly TimeSpan ShutdownBound = TimeSpan.FromSeconds(15);
    private static readonly string[] AzureAuthProbeScopes = ["https://communication.azure.com/.default"];

    /// <summary>
    /// Deadline for the initial Azure authentication probe. It is linked to the form lifetime,
    /// so closing the window cancels it and an expired deadline surfaces inline Error + Retry
    /// instead of leaving the checklist stuck on "Azure authentication: InProgress".
    /// </summary>
    internal static readonly TimeSpan AzureAuthProbeTimeout = AzureAuthenticationProbe.DefaultTimeout;

    private readonly SimulatorSettings _settings;
    private readonly IServiceProvider _services;
    private readonly SimulatorController _controller = new();
    private readonly TranscriptPresenter _transcriptPresenter = new();
    private readonly CallGenerationGate _callGate = new();
    private readonly System.Windows.Forms.Timer _elapsedTimer = new() { Interval = 1000 };
    private readonly CancellationTokenSource _formLifetime = new();
    private readonly object _tunnelCleanupSync = new();
    private readonly IDevTunnelSessionWatcher _tunnelSessionWatcher;
    private readonly TunnelFailureCleanupCoordinator _tunnelFailureCleanupCoordinator = new();

    private IReadOnlyList<CallerScriptPreset> _presets = [];
    private DevTunnelSession? _tunnelSession;
    private DevTunnelSession? _cleaningTunnelSession;
    private Task<TunnelCleanupResult>? _tunnelCleanupTask;
    private Task? _tunnelExitWatchTask;
    private IUiEventDispatcher? _uiEventDispatcher;
    private ActiveCallResources? _active;
    private Task? _callStartupTask;
    private DevTunnelSession? _callStartupTunnelSession;
    private long _callStartupTunnelGeneration;
    private CallerScriptDraft? _baselineDraft;
    private bool _suppressPresetSelectionEvent;
    private bool _suppressCallerIdSelectionEvent;
    private bool _suppressDestinationTextEvent;
    private bool _suppressScriptFieldEvents;
    private bool _initializationStarted;
    private readonly bool _suppressInitializationOnShown;
    private bool _compositionResourcesDisposed;
    private bool _closeConfirmed;
    private bool _shutdownInProgress;
    private Stopwatch? _elapsedStopwatch;
    private InitializationStage _currentInitializingStage = InitializationStage.AzureAuthentication;
    private long _tunnelSessionGeneration;

    /// <summary>
    /// Initializes the window with an already-loaded configuration and an already-built
    /// composition root. Neither parameter is touched here beyond field assignment, so
    /// constructing this form never performs Azure, tunnel, Speech, model, PSTN, or audio
    /// device access; that work happens only after <see cref="OnShown"/> fires.
    /// </summary>
    public MainForm(SimulatorSettings settings, IServiceProvider services)
        : this(settings, services, new DevTunnelSessionWatcher())
    {
    }

    internal MainForm(
        SimulatorSettings settings,
        IServiceProvider services,
        IDevTunnelSessionWatcher tunnelSessionWatcher)
        : this(settings, services, tunnelSessionWatcher, suppressInitializationOnShown: false)
    {
    }

    /// <summary>
    /// Test-only constructor. When <paramref name="suppressInitializationOnShown"/> is true the
    /// window can be shown and laid out for real (so DPI-dependent measurements are meaningful)
    /// while <see cref="OnShown"/> performs no Azure, Dev Tunnel, Speech, model, PSTN, or
    /// audio-device work.
    /// </summary>
    internal MainForm(
        SimulatorSettings settings,
        IServiceProvider services,
        IDevTunnelSessionWatcher tunnelSessionWatcher,
        bool suppressInitializationOnShown)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _tunnelSessionWatcher = tunnelSessionWatcher
            ?? throw new ArgumentNullException(nameof(tunnelSessionWatcher));
        _suppressInitializationOnShown = suppressInitializationOnShown;

        InitializeComponent();

        _controller.StateChanged += (_, state) => MarshalToUi(() => ApplyState(state));
        _transcriptPresenter.Changed += (_, change) => MarshalToUi(() => ApplyTranscriptChange(change));
        _transcriptPresenter.Cleared += (_, _) => MarshalToUi(ClearTranscriptDisplay);
        _elapsedTimer.Tick += OnElapsedTimerTick;
        FormClosing += OnMainFormClosing;

        ApplyState(_controller.State);
    }

    /// <summary>
    /// Exposed for the STA layout smoke test only, so it can assert on the current
    /// controller-derived view state without depending on WinForms control internals.
    /// </summary>
    internal SimulatorViewState CurrentViewState => _controller.State;

    /// <summary>
    /// Creates a form that can safely be shown and laid out by the STA layout tests: it is fully
    /// composed and measured for real, but <see cref="OnShown"/> starts no external
    /// initialization.
    /// </summary>
    internal static MainForm CreateForLayoutMeasurement(
        SimulatorSettings settings,
        IServiceProvider services) =>
        new(settings, services, new DevTunnelSessionWatcher(), suppressInitializationOnShown: true);

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_suppressInitializationOnShown || _initializationStarted)
        {
            return;
        }

        _initializationStarted = true;
        _uiEventDispatcher = new SerializedUiEventDispatcher(
            SynchronizationContext.Current
            ?? throw new InvalidOperationException("The main form requires a UI synchronization context."));
        _ = RunInitializationAsync();
    }

    // ---- Startup --------------------------------------------------------------------------

    private DevTunnelSession CreateTunnelSession()
    {
        var session = new DevTunnelSession(
            new CallbackHost(new CallbackHostOptions { Port = _settings.Acs.LocalCallbackPort }));
        session.CallbackHostStarted += OnDevTunnelCallbackHostStarted;
        session.SignInRequired += OnDevTunnelSignInRequired;
        return session;
    }

    private void OnDevTunnelCallbackHostStarted(object? sender, EventArgs e) =>
        MarshalToUi(() => _controller.ReportStageCompleted(InitializationStage.CallbackHost));

    private void OnDevTunnelSignInRequired(object? sender, EventArgs e) =>
        MarshalToUi(() => _controller.BeginSignIn());

    private async Task RunInitializationAsync()
    {
        await RunOnUiAsync(_controller.BeginInitialization);
        try
        {
            _currentInitializingStage = InitializationStage.AzureAuthentication;
            await RunOnUiAsync(() => _controller.ReportStageStarted(InitializationStage.AzureAuthentication));
            var credential = _services.GetRequiredService<TokenCredential>();
            await AzureAuthenticationProbe.ExecuteAsync(
                token => credential.GetTokenAsync(
                    new TokenRequestContext(AzureAuthProbeScopes),
                    token).AsTask(),
                AzureAuthProbeTimeout,
                _formLifetime.Token).ConfigureAwait(false);
            await RunOnUiAsync(() => _controller.ReportStageCompleted(InitializationStage.AzureAuthentication));

            _currentInitializingStage = InitializationStage.NumberDiscovery;
            await RunOnUiAsync(() => _controller.ReportStageStarted(InitializationStage.NumberDiscovery));
            var discovery = _services.GetRequiredService<AcsPhoneNumberDiscovery>();
            var discoveryResult = await discovery.DiscoverAsync(_formLifetime.Token).ConfigureAwait(false);
            await RunOnUiAsync(() => _controller.ReportStageCompleted(InitializationStage.NumberDiscovery));

            _currentInitializingStage = InitializationStage.CallbackHost;
            await RunOnUiAsync(() => _controller.ReportStageStarted(InitializationStage.CallbackHost));
            await RunOnUiAsync(() => _controller.ReportStageStarted(InitializationStage.DevTunnel));
            _currentInitializingStage = InitializationStage.DevTunnel;

            // Assign ownership before starting. A failed StartAsync may already have started
            // Kestrel or created the temporary tunnel, and the initialization failure path
            // must dispose that partial session rather than losing it.
            var tunnelSession = CreateTunnelSession();
            var tunnelGeneration = AssignTunnelSession(tunnelSession);
            await tunnelSession.StartAsync(_formLifetime.Token).ConfigureAwait(false);
            StartTunnelExitWatcher(tunnelSession, tunnelGeneration);
            await RunOnUiAsync(() => _controller.ReportStageCompleted(InitializationStage.DevTunnel));
            if (_controller.State.Phase == AppPhase.SignInRequired)
            {
                await RunOnUiAsync(_controller.EndSignIn);
            }

            _presets = CallerScriptPresetCatalog.CreateDefaultPresets(_settings);

            await RunOnUiAsync(() => _controller.CompleteInitialization(
                discoveryResult,
                _settings.Acs.DefaultDestination,
                _presets.Select(preset => preset.Name).ToArray(),
                tunnelSession.PublicEventUri.Host,
                _settings.AiServices.TextDeployment));

            if (_presets.Count > 0)
            {
                await RunOnUiAsync(() => _controller.RequestPresetSelection(
                    0,
                    _presets[0].CreateDraft(),
                    isDirty: false));
                _baselineDraft = _presets[0].CreateDraft();
            }
        }
        catch (Exception ex)
        {
            var safeMessage = SafeMessage(ex);
            await DisposeTunnelSessionAsync();
            await RunOnUiAsync(() => _controller.ReportStageFailed(_currentInitializingStage, safeMessage));
            AppendDiagnosticsMarshaled($"Initialization failed: {safeMessage}");
        }
    }

    private void OnRetryButtonClick(object? sender, EventArgs e)
    {
        if (!_controller.BeginRetry())
        {
            return;
        }

        _ = RunRetryAsync();
    }

    private async Task RunRetryAsync()
    {
        try
        {
            var cleanup = await DisposeTunnelSessionAsync().ConfigureAwait(false);
            if (!cleanup.Succeeded)
            {
                await RunOnUiAsync(() => _controller.ReportStageFailed(
                    InitializationStage.DevTunnel,
                    "The previous Dev Tunnel could not be cleaned up. Retry cleanup before initializing again."));
                return;
            }

            await RunInitializationAsync().ConfigureAwait(false);
        }
        finally
        {
            MarshalToUi(_controller.EndRetry);
        }
    }

    private long AssignTunnelSession(DevTunnelSession session)
    {
        lock (_tunnelCleanupSync)
        {
            _tunnelSession = session;
            return Interlocked.Increment(ref _tunnelSessionGeneration);
        }
    }

    private void StartTunnelExitWatcher(DevTunnelSession session, long sessionGeneration)
    {
        // Keep the task rooted for the lifetime of the form. The default watcher and this
        // boundary both absorb faults, so a Dev Tunnel host failure can never become an
        // unobserved task exception.
        _tunnelExitWatchTask = ObserveTunnelExitAsync(session, sessionGeneration);
    }

    private async Task ObserveTunnelExitAsync(DevTunnelSession session, long sessionGeneration)
    {
        try
        {
            await _tunnelSessionWatcher.WatchAsync(
                session,
                () => IsCurrentTunnelSession(session, sessionGeneration),
                () => HandleUnexpectedTunnelExitAsync(session, sessionGeneration),
                _formLifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_formLifetime.IsCancellationRequested)
        {
            // Ordered form shutdown owns tunnel cleanup.
        }
        catch
        {
            try
            {
                if (IsCurrentTunnelSession(session, sessionGeneration))
                {
                    await HandleUnexpectedTunnelExitAsync(session, sessionGeneration).ConfigureAwait(false);
                }
            }
            catch
            {
                // The form may be disposing concurrently. The watcher must never fault.
            }
        }
    }

    private Task HandleUnexpectedTunnelExitAsync(DevTunnelSession session, long sessionGeneration) =>
        _tunnelFailureCleanupCoordinator.HandleAsync(
            () => IsCurrentTunnelSession(session, sessionGeneration),
            () => BeginUnexpectedTunnelFailureCleanupAsync(session, sessionGeneration),
            () => HangUpAndFinalizeTunnelCallAsync(session, sessionGeneration),
            async () => _ = await DisposeTunnelSessionAsync(session).ConfigureAwait(false),
            () => RunOnUiAsync(_controller.CompleteTunnelFailureCleanup));

    private async Task<bool> BeginUnexpectedTunnelFailureCleanupAsync(
        DevTunnelSession session,
        long sessionGeneration)
    {
        var handled = false;
        await RunOnUiAsync(() =>
        {
            if (!IsCurrentTunnelSession(session, sessionGeneration))
            {
                return;
            }

            handled = true;
            _controller.BeginTunnelFailureCleanup(
                "The Dev Tunnel stopped unexpectedly. Retiring the active call before Retry.");

            var callGeneration = _callGate.Current;
            if (callGeneration != 0)
            {
                // Prevent an in-flight dial attempt from activating after the tunnel that
                // created it has failed. Its startup task will release any unstarted resources.
                _callGate.Retire(callGeneration);
            }

            AppendDiagnostics("Dev Tunnel host stopped unexpectedly. Cleaning up the active call before Retry.");
        }).ConfigureAwait(false);

        return handled;
    }

    private async Task HangUpAndFinalizeTunnelCallAsync(
        DevTunnelSession session,
        long sessionGeneration)
    {
        ActiveCallResources? active = null;
        Task? callStartupTask = null;
        await RunOnUiAsync(() =>
        {
            if (_active is { } current
                && current.UsesTunnelSession(session, sessionGeneration))
            {
                active = current;
            }

            if (ReferenceEquals(_callStartupTunnelSession, session)
                && _callStartupTunnelGeneration == sessionGeneration)
            {
                callStartupTask = _callStartupTask;
            }
        }).ConfigureAwait(false);

        if (active is not null)
        {
            await RequestHangUpAndFinalizeCallAsync(
                active,
                "The Dev Tunnel stopped unexpectedly.").ConfigureAwait(false);
        }

        if (callStartupTask is not null)
        {
            await AwaitCallStartupCleanupAsync(callStartupTask).ConfigureAwait(false);
        }
    }

    private bool IsCurrentTunnelSession(DevTunnelSession session, long sessionGeneration)
    {
        lock (_tunnelCleanupSync)
        {
            return ReferenceEquals(_tunnelSession, session)
                && Interlocked.Read(ref _tunnelSessionGeneration) == sessionGeneration;
        }
    }

    // ---- Routing ----------------------------------------------------------------------------

    private void OnRefreshNumbersButtonClick(object? sender, EventArgs e)
    {
        if (!_controller.BeginRefreshNumbers())
        {
            return;
        }

        _ = RunRefreshNumbersAsync();
    }

    private async Task RunRefreshNumbersAsync()
    {
        try
        {
            var discovery = _services.GetRequiredService<AcsPhoneNumberDiscovery>();
            var result = await discovery.DiscoverAsync(_formLifetime.Token).ConfigureAwait(false);
            MarshalToUi(() => _controller.CompleteRefreshNumbers(result));
        }
        catch (Exception ex)
        {
            var safeMessage = SafeMessage(ex);
            MarshalToUi(() => _controller.FailRefreshNumbers(safeMessage));
            AppendDiagnosticsMarshaled($"Number discovery refresh failed: {safeMessage}");
        }
    }

    private void OnCallerIdComboBoxSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_suppressCallerIdSelectionEvent)
        {
            return;
        }

        var value = CallerIdComboBox.SelectedItem as string;
        _controller.SelectCallerId(value);
    }

    private void OnDestinationTextBoxTextChanged(object? sender, EventArgs e)
    {
        if (_suppressDestinationTextEvent)
        {
            return;
        }

        _controller.SetDestination(DestinationTextBox.Text);
    }

    // ---- Caller script presets ---------------------------------------------------------------

    private void OnPresetComboBoxSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_suppressPresetSelectionEvent)
        {
            return;
        }

        var index = PresetComboBox.SelectedIndex;
        if (index < 0 || index >= _presets.Count || index == _controller.State.SelectedPresetIndex)
        {
            return;
        }

        var candidateDraft = _presets[index].CreateDraft();
        var isDirty = !CallerScriptDraftComparer.AreEqual(_controller.State.Draft, _baselineDraft);

        if (_controller.RequestPresetSelection(index, candidateDraft, isDirty))
        {
            _baselineDraft = candidateDraft.Clone();
            return;
        }

        var confirmed = ConfirmDiscardScriptChanges();
        _controller.ConfirmPendingPresetSelection(confirmed);

        if (confirmed)
        {
            _baselineDraft = candidateDraft.Clone();
        }
        else
        {
            _suppressPresetSelectionEvent = true;
            PresetComboBox.SelectedIndex = _controller.State.SelectedPresetIndex;
            _suppressPresetSelectionEvent = false;
        }
    }

    /// <summary>
    /// Shows the one confirmation dialog used when switching presets would discard unsaved
    /// script edits. Kept as a single small method (rather than inlined) so the surrounding
    /// selection/confirm/decline logic above is easy to read and reason about; that logic
    /// itself is exercised directly at the <see cref="SimulatorController"/> level in tests.
    /// </summary>
    private bool ConfirmDiscardScriptChanges()
    {
        var result = MessageBox.Show(
            this,
            "This preset has unsaved edits. Switching presets will discard them. Continue?",
            "Discard caller script changes?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return result == DialogResult.Yes;
    }

    private void OnResetPresetButtonClick(object? sender, EventArgs e)
    {
        if (_controller.State.SelectedPresetIndex is < 0 || _controller.State.SelectedPresetIndex >= _presets.Count)
        {
            return;
        }

        var draft = _presets[_controller.State.SelectedPresetIndex].CreateDraft();
        _controller.ResetDraftToPreset(draft);
        _baselineDraft = draft.Clone();
    }

    private void OnScriptFieldTextChanged(object? sender, EventArgs e)
    {
        if (_suppressScriptFieldEvents || _controller.State.Draft is null)
        {
            return;
        }

        var current = _controller.State.Draft;
        var updated = new CallerScriptDraft
        {
            Name = current.Name,
            Locale = current.Locale,
            Voice = current.Voice,
            OpeningLine = current.OpeningLine,
            Identity = IdentityTextBox.Text,
            Background = BackgroundTextBox.Text,
            Reason = ReasonTextBox.Text,
            Urgency = UrgencyTextBox.Text,
            CallbackNumber = CallbackNumberTextBox.Text,
            AdditionalDetails = AdditionalDetailsTextBox.Text,
        };
        _controller.UpdateDraft(updated);
    }

    // ---- Call lifecycle -----------------------------------------------------------------------

    private void OnCallButtonClick(object? sender, EventArgs e)
    {
        if (!TryGetCurrentTunnelSession(out var tunnelSession, out var tunnelSessionGeneration))
        {
            return;
        }

        if (!_controller.BeginDial())
        {
            return;
        }

        var generation = _callGate.Advance();
        var callerId = _controller.State.SelectedCallerId!;
        var destination = _controller.State.Destination;
        _callStartupTunnelSession = tunnelSession;
        _callStartupTunnelGeneration = tunnelSessionGeneration;
        _callStartupTask = CreateAndRunCallAsync(
            generation,
            callerId,
            destination,
            tunnelSession,
            tunnelSessionGeneration);
    }

    private async Task CreateAndRunCallAsync(
        long generation,
        string callerId,
        string destination,
        DevTunnelSession tunnelSession,
        long tunnelSessionGeneration)
    {
        ActiveCallResources active;
        try
        {
            active = await CreateActiveCallResourcesAsync(
                generation,
                tunnelSession,
                tunnelSessionGeneration).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var safeMessage = SafeMessage(ex);
            _callGate.Retire(generation);
            await RunOnUiAsync(() => _controller.CompleteCall(safeMessage)).ConfigureAwait(false);
            AppendDiagnosticsMarshaled($"Unable to start the call: {safeMessage}");
            return;
        }

        var activated = false;
        await RunOnUiAsync(() =>
        {
            if (!_callGate.IsCurrent(generation)
                || !IsCurrentTunnelSession(tunnelSession, tunnelSessionGeneration))
            {
                return;
            }

            _active = active;
            SubscribeActiveCallEvents(active);
            StartElapsedTimer();
            _lastAppendedCallStatusMessage = null;
            activated = true;
        }).ConfigureAwait(false);

        if (!activated)
        {
            await DisposeUnstartedCallResourcesAsync(active).ConfigureAwait(false);
            await RunOnUiAsync(() => _controller.CompleteCall("The call was cancelled before dialing."))
                .ConfigureAwait(false);
            return;
        }

        await RunCallAsync(active, generation, callerId, destination).ConfigureAwait(false);
    }

    private async Task<ActiveCallResources> CreateActiveCallResourcesAsync(
        long generation,
        DevTunnelSession tunnelSession,
        long tunnelSessionGeneration)
    {
        var draft = _controller.State.Draft
            ?? throw new InvalidOperationException("No caller script preset is selected.");

        var gateway = _services.GetRequiredService<ICallAutomationGateway>();
        var speechFactory = _services.GetRequiredService<ISpeechPipelineFactory>();
        var monitorFactory = _services.GetRequiredService<IAudioMonitorFactory>();
        var replyGenerator = _services.GetRequiredService<IGroundedReplyGenerator>();

        var resources = await SimulatorCallComposition.CreateAsync(
            draft,
            MuteLocalAudioCheckBox.Checked,
            () => new AcsCallSession(tunnelSession, gateway),
            speechFactory,
            monitorFactory,
            replyGenerator,
            fault => AppendDiagnosticsMarshaled($"Local audio monitor ({fault.Operation}): {fault.Message}"))
            .ConfigureAwait(false);
        var router = new CallEventRouter(
            _controller,
            _transcriptPresenter,
            _callGate,
            generation,
            AppendDiagnosticsMarshaled,
            _uiEventDispatcher
                ?? throw new InvalidOperationException("The UI event dispatcher has not been initialized."));

        return new ActiveCallResources(
            resources.CallSession,
            resources.Orchestrator,
            resources.Monitor,
            resources.Script,
            router,
            tunnelSession,
            tunnelSessionGeneration);
    }

    private void SubscribeActiveCallEvents(ActiveCallResources active)
    {
        active.CallSession.StateChanged += active.Router.HandleCallStateChanged;
        active.Orchestrator.TranscriptUpdated += active.Router.HandleTranscriptUpdated;
        active.Orchestrator.ActivityChanged += active.Router.HandleActivityChanged;
        active.Monitor.Faulted += active.Router.HandleAudioMonitorFaulted;
        active.CallEndedHandler = change => OnRoutedCallEnded(active, change);
        active.Router.CallEnded += active.CallEndedHandler;
    }

    private void UnsubscribeActiveCallEvents(ActiveCallResources active)
    {
        active.CallSession.StateChanged -= active.Router.HandleCallStateChanged;
        active.Orchestrator.TranscriptUpdated -= active.Router.HandleTranscriptUpdated;
        active.Orchestrator.ActivityChanged -= active.Router.HandleActivityChanged;
        active.Monitor.Faulted -= active.Router.HandleAudioMonitorFaulted;
        if (active.CallEndedHandler is not null)
        {
            active.Router.CallEnded -= active.CallEndedHandler;
            active.CallEndedHandler = null;
        }
    }

    private void OnRoutedCallEnded(ActiveCallResources active, CallStateChange change)
    {
        // CallEventRouter invokes this from its serialized UI dispatch handler.
        _ = FinalizeCallAsync(active, change.Reason);
    }

    private async Task RunCallAsync(ActiveCallResources active, long generation, string callerId, string destination)
    {
        try
        {
            await active.CallSession.StartAsync(callerId, destination, _formLifetime.Token).ConfigureAwait(false);
            await active.Orchestrator.StartAsync(_formLifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var safeMessage = SafeMessage(ex);
            AppendDiagnosticsMarshaled($"Call failed: {safeMessage}");
            await FinalizeCallAsync(active, safeMessage).ConfigureAwait(false);
        }
    }

    private void OnHangUpButtonClick(object? sender, EventArgs e)
    {
        if (!_controller.BeginHangUp())
        {
            return;
        }

        var active = _active;
        if (active is null)
        {
            // A resource-construction failure may be asynchronously rolling back after
            // BeginDial. Retire its generation so it cannot activate after this Hang Up.
            var generation = _callGate.Current;
            if (generation != 0)
            {
                _callGate.Retire(generation);
            }

            return;
        }

        _ = RunHangUpAsync(active);
    }

    private async Task RunHangUpAsync(ActiveCallResources active)
    {
        try
        {
            await active.CallSession.HangUpAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppendDiagnosticsMarshaled($"Hang up reported: {SafeMessage(ex)}");
        }
    }

    private async Task RequestHangUpAndFinalizeCallAsync(
        ActiveCallResources active,
        string safeReason,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await active.CallSession.HangUpAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppendDiagnosticsMarshaled($"Hang up reported: {SafeMessage(ex)}");
        }

        await FinalizeCallAsync(active, safeReason).ConfigureAwait(false);
    }

    private async Task AwaitCallStartupCleanupAsync(Task callStartupTask)
    {
        try
        {
            await callStartupTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppendDiagnosticsMarshaled($"Call startup cleanup reported: {SafeMessage(exception)}");
        }
    }

    private Task FinalizeCallAsync(ActiveCallResources active, string safeReason) =>
        active.BeginFinalize(() => FinalizeCallCoreAsync(active, safeReason));

    private async Task FinalizeCallCoreAsync(ActiveCallResources active, string safeReason)
    {
        // Retiring before queueing UI teardown makes any already-posted worker event stale.
        // The router rechecks the generation in its serialized UI handler before each reducer
        // or transcript mutation.
        active.Router.Retire();
        await RunOnUiAsync(() =>
        {
            StopElapsedTimer();
            UnsubscribeActiveCallEvents(active);
        }).ConfigureAwait(false);

        await SimulatorCallComposition.DisposeAsync(
            active.Resources,
            ex => AppendDiagnosticsMarshaled($"Conversation cleanup reported: {SafeMessage(ex)}"),
            ex => AppendDiagnosticsMarshaled($"Call cleanup reported: {SafeMessage(ex)}")).ConfigureAwait(false);

        await RunOnUiAsync(() =>
        {
            if (ReferenceEquals(_active, active))
            {
                _active = null;
            }

            _controller.CompleteCall(safeReason);
        }).ConfigureAwait(false);
    }

    private async Task DisposeUnstartedCallResourcesAsync(ActiveCallResources active)
    {
        active.Router.Retire();
        await SimulatorCallComposition.DisposeAsync(
            active.Resources,
            exception => AppendDiagnosticsMarshaled($"Conversation cleanup reported: {SafeMessage(exception)}"),
            exception => AppendDiagnosticsMarshaled($"Call cleanup reported: {SafeMessage(exception)}"))
            .ConfigureAwait(false);
    }

    private void OnMuteLocalAudioCheckBoxCheckedChanged(object? sender, EventArgs e)
    {
        _controller.SetMuted(MuteLocalAudioCheckBox.Checked);
        if (_active is { } active)
        {
            active.Monitor.IsMuted = MuteLocalAudioCheckBox.Checked;
        }
    }

    private void OnClearTranscriptButtonClick(object? sender, EventArgs e)
    {
        if (!SimulatorController.IsClearTranscriptEnabled(_controller.State))
        {
            return;
        }

        _transcriptPresenter.Clear(); // raises Cleared, which resets the RichTextBox in lockstep
        _controller.SetHasTranscript(false);
    }

    private void OnCopyCallbackHostButtonClick(object? sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(CallbackHostTextBox.Text))
        {
            Clipboard.SetText(CallbackHostTextBox.Text);
        }
    }

    private void StartElapsedTimer()
    {
        _elapsedStopwatch = Stopwatch.StartNew();
        _elapsedTimer.Start();
    }

    private void StopElapsedTimer()
    {
        _elapsedTimer.Stop();
        _elapsedStopwatch = null;
    }

    private void OnElapsedTimerTick(object? sender, EventArgs e)
    {
        if (_elapsedStopwatch is { } stopwatch)
        {
            _controller.Tick(stopwatch.Elapsed);
        }
    }

    // ---- Shutdown -------------------------------------------------------------------------

    private void OnMainFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        e.Cancel = true;

        if (_shutdownInProgress)
        {
            // A close was already requested once and its bounded cleanup is still running;
            // do not start a second concurrent shutdown sequence.
            return;
        }

        var active = _active;
        var tunnelSession = GetOwnedTunnelSession();
        if (active is null && tunnelSession is null)
        {
            _closeConfirmed = true;
            e.Cancel = false;
            return;
        }

        _shutdownInProgress = true;
        _formLifetime.Cancel();
        _ = RunOrderedShutdownAsync(active, tunnelSession);
    }

    private async Task RunOrderedShutdownAsync(ActiveCallResources? active, DevTunnelSession? tunnelSession)
    {
        AppendDiagnosticsMarshaled("Shutting down: ending any active call before stopping the Dev Tunnel.");

        var outcome = await ShutdownSequencer.RunAsync(
            async token =>
            {
                await HangUpAndFinalizeForShutdownAsync(active, token).ConfigureAwait(false);
            },
            async token =>
            {
                if (tunnelSession is not null)
                {
                    var cleanup = await DisposeTunnelSessionAsync(tunnelSession).ConfigureAwait(false);
                    if (!cleanup.Succeeded)
                    {
                        throw new InvalidOperationException("The owned Dev Tunnel cleanup did not complete.");
                    }
                }
            },
            ShutdownBound,
            CancellationToken.None).ConfigureAwait(false);

        var tunnelRetained = tunnelSession is not null && IsTunnelSessionRetained(tunnelSession);
        if (outcome != ShutdownOutcome.Completed)
        {
            AppendDiagnosticsMarshaled($"Shutdown cleanup did not complete cleanly ({outcome}).");
        }

        // Disposing the shared DI service provider happens after this method returns, once
        // Program.cs's `using` block regains control from Application.Run — i.e. strictly
        // after hang-up/call cleanup and tunnel stop/delete have completed above.
        MarshalToUi(() =>
        {
            _active = null;
            if (tunnelRetained)
            {
                _shutdownInProgress = false;
                _controller.ReportStageFailed(
                    InitializationStage.DevTunnel,
                    "The owned Dev Tunnel is still being retained for cleanup. Close again to retry.");
                AppendDiagnostics("The window remains open because the owned Dev Tunnel was not deleted.");
                return;
            }

            _tunnelSession = null;
            _closeConfirmed = true;
            Close();
        });
    }

    private async Task HangUpAndFinalizeForShutdownAsync(
        ActiveCallResources? active,
        CancellationToken cancellationToken)
    {
        Task? callStartupTask = null;
        await RunOnUiAsync(() =>
        {
            var callGeneration = _callGate.Current;
            if (callGeneration != 0)
            {
                _callGate.Retire(callGeneration);
            }

            active ??= _active;
            callStartupTask = _callStartupTask;
            active?.Router.Retire();
            if (active is not null)
            {
                UnsubscribeActiveCallEvents(active);
            }
        }).ConfigureAwait(false);

        if (active is not null)
        {
            await RequestHangUpAndFinalizeCallAsync(
                active,
                "Application shutdown requested.",
                cancellationToken).ConfigureAwait(false);
        }

        if (callStartupTask is not null)
        {
            await AwaitCallStartupCleanupAsync(callStartupTask).ConfigureAwait(false);
        }
    }

    private void DisposeCompositionResources()
    {
        // Form.Close() on a modeless window already disposes the form, so an outer using/Dispose
        // reaches here a second time. Cancelling a disposed CancellationTokenSource throws, which
        // would surface as an unhandled exception on the UI thread.
        if (_compositionResourcesDisposed)
        {
            return;
        }

        _compositionResourcesDisposed = true;
        _formLifetime.Cancel();
        _formLifetime.Dispose();
        _elapsedTimer.Dispose();
    }

    // ---- State application ------------------------------------------------------------------

    private void ApplyState(SimulatorViewState state)
    {
        StatusBannerLabel.Text = SimulatorController.DescribePhase(state.Phase);

        ApplyChecklist(state);

        CallbackHostTextBox.Text = state.PublicCallbackHost ?? string.Empty;
        SelectedModelTextBox.Text = state.SelectedModel ?? string.Empty;

        var errorMessage = state.InitializationError ?? state.RefreshError;
        var hasError = state.Phase == AppPhase.Error || !string.IsNullOrEmpty(state.RefreshError);
        InitializationErrorLabel.Visible = hasError;
        InitializationErrorLabel.Text = errorMessage ?? string.Empty;
        RetryButton.Visible = state.Phase == AppPhase.Error;
        RetryButton.Enabled = SimulatorController.IsRetryEnabled(state);

        ApplyCallerIds(state);
        DestinationTextBox.Enabled = !state.IsSetupLocked;
        RefreshNumbersButton.Enabled = SimulatorController.IsRefreshEnabled(state);

        var validDestination = SimulatorController.IsDestinationValid(state);
        RoutingErrorProvider.SetError(DestinationTextBox, validDestination ? string.Empty : "Enter a valid E.164 destination number.");
        RoutingErrorProvider.SetError(CallerIdComboBox, SimulatorController.IsCallerIdValid(state) ? string.Empty : "Select a valid caller ID.");

        ApplyPresetSelection(state);
        ApplyDraft(state.Draft);

        var callbackValid = SimulatorController.IsCallbackNumberValid(state);
        RoutingErrorProvider.SetError(CallbackNumberTextBox, callbackValid ? string.Empty : "Enter a valid E.164 callback number.");

        var setupLocked = state.IsSetupLocked;
        RoutingGroupBox.Enabled = !setupLocked;
        ScriptGroupBox.Enabled = !setupLocked;

        CallStateValueLabel.Text = state.CallState?.ToString() ?? "-";
        ElapsedValueLabel.Text = state.Elapsed.ToString(@"mm\:ss");
        CallerIdValueLabel.Text = state.IsCallInProgress ? state.SelectedCallerId ?? "-" : "-";
        DestinationValueLabel.Text = state.IsCallInProgress ? state.Destination : "-";
        ActivityValueLabel.Text = state.Activity?.ToString() ?? "-";

        CallButton.Enabled = SimulatorController.IsCallEnabled(state);
        HangUpButton.Enabled = SimulatorController.IsHangUpEnabled(state);
        MuteLocalAudioCheckBox.Enabled = true;
        CopyCallbackHostButton.Enabled = !state.IsCallInProgress
            && !string.IsNullOrEmpty(state.PublicCallbackHost);
        ClearTranscriptButton.Enabled = SimulatorController.IsClearTranscriptEnabled(state);
        CallDisabledReasonLabel.Text = SimulatorController.DescribeCallDisabledReason(state);

        if (!string.IsNullOrEmpty(state.CallStatusMessage)
            && !state.IsCallInProgress
            && !string.Equals(state.CallStatusMessage, _lastAppendedCallStatusMessage, StringComparison.Ordinal))
        {
            _lastAppendedCallStatusMessage = state.CallStatusMessage;
            AppendDiagnostics($"Call ended: {state.CallStatusMessage}");
        }
    }

    private string? _lastAppendedCallStatusMessage;

    private void ApplyChecklist(SimulatorViewState state)
    {
        foreach (var item in state.Checklist)
        {
            var label = item.Stage switch
            {
                InitializationStage.AzureAuthentication => AzureAuthChecklistLabel,
                InitializationStage.NumberDiscovery => NumberDiscoveryChecklistLabel,
                InitializationStage.CallbackHost => CallbackHostChecklistLabel,
                InitializationStage.DevTunnel => DevTunnelChecklistLabel,
                _ => null,
            };
            if (label is null)
            {
                continue;
            }

            var caption = item.Stage switch
            {
                InitializationStage.AzureAuthentication => "Azure authentication",
                InitializationStage.NumberDiscovery => "Number discovery",
                InitializationStage.CallbackHost => "Callback host",
                InitializationStage.DevTunnel => "Dev Tunnel",
                _ => item.Stage.ToString(),
            };
            label.Text = $"{caption}: {item.Status}";
        }
    }

    private void ApplyCallerIds(SimulatorViewState state)
    {
        var currentItems = CallerIdComboBox.Items.Cast<string>().ToArray();
        if (!currentItems.SequenceEqual(state.CallerIds, StringComparer.Ordinal))
        {
            CallerIdComboBox.BeginUpdate();
            CallerIdComboBox.Items.Clear();
            foreach (var callerId in state.CallerIds)
            {
                CallerIdComboBox.Items.Add(callerId);
            }

            CallerIdComboBox.EndUpdate();
        }

        CallerIdComboBox.Enabled = !state.IsSetupLocked;

        var desired = state.SelectedCallerId;
        if (desired is null)
        {
            _suppressCallerIdSelectionEvent = true;
            CallerIdComboBox.SelectedIndex = -1;
            _suppressCallerIdSelectionEvent = false;
        }
        else if (!Equals(CallerIdComboBox.SelectedItem, desired))
        {
            _suppressCallerIdSelectionEvent = true;
            CallerIdComboBox.SelectedItem = desired;
            _suppressCallerIdSelectionEvent = false;
        }

        if (!string.Equals(DestinationTextBox.Text, state.Destination, StringComparison.Ordinal)
            && !DestinationTextBox.Focused)
        {
            _suppressDestinationTextEvent = true;
            DestinationTextBox.Text = state.Destination;
            _suppressDestinationTextEvent = false;
        }
    }

    private void ApplyPresetSelection(SimulatorViewState state)
    {
        if (PresetComboBox.Items.Count != state.PresetNames.Count)
        {
            _suppressPresetSelectionEvent = true;
            PresetComboBox.Items.Clear();
            foreach (var name in state.PresetNames)
            {
                PresetComboBox.Items.Add(name);
            }

            _suppressPresetSelectionEvent = false;
        }

        if (PresetComboBox.SelectedIndex != state.SelectedPresetIndex)
        {
            _suppressPresetSelectionEvent = true;
            PresetComboBox.SelectedIndex = state.SelectedPresetIndex;
            _suppressPresetSelectionEvent = false;
        }

        PresetComboBox.Enabled = !state.IsSetupLocked;
        ResetPresetButton.Enabled = !state.IsSetupLocked && state.SelectedPresetIndex >= 0;
    }

    private void ApplyDraft(CallerScriptDraft? draft)
    {
        _suppressScriptFieldEvents = true;
        try
        {
            LocaleValueLabel.Text = draft?.Locale ?? string.Empty;
            VoiceValueLabel.Text = draft?.Voice ?? string.Empty;

            SetTextIfChanged(IdentityTextBox, draft?.Identity ?? string.Empty);
            SetTextIfChanged(BackgroundTextBox, draft?.Background ?? string.Empty);
            SetTextIfChanged(ReasonTextBox, draft?.Reason ?? string.Empty);
            SetTextIfChanged(UrgencyTextBox, draft?.Urgency ?? string.Empty);
            SetTextIfChanged(CallbackNumberTextBox, draft?.CallbackNumber ?? string.Empty);
            SetTextIfChanged(AdditionalDetailsTextBox, draft?.AdditionalDetails ?? string.Empty);
        }
        finally
        {
            _suppressScriptFieldEvents = false;
        }
    }

    private static void SetTextIfChanged(TextBox textBox, string value)
    {
        if (!string.Equals(textBox.Text, value, StringComparison.Ordinal))
        {
            textBox.Text = value;
        }
    }

    private void ApplyTranscriptChange(TranscriptPresenterChange change)
    {
        var line = _transcriptPresenter.Lines[change.Index];
        var formatted = FormatTranscriptLine(line);
        var color = GetTranscriptColor(line.Speaker);

        if (change.Replaced)
        {
            ReplaceTranscriptDisplayLine(change.Index, formatted, color);
        }
        else
        {
            AppendTranscriptDisplayLine(formatted, color);
        }
    }

    private void ClearTranscriptDisplay()
    {
        TranscriptRichTextBox.Clear();
        _transcriptLineStarts.Clear();
    }

    private static string FormatTranscriptLine(PresentedTranscriptLine line)
    {
        var speakerLabel = line.Speaker switch
        {
            TranscriptSpeaker.Caller => "Caller",
            TranscriptSpeaker.ServiceDesk => "Service Desk",
            TranscriptSpeaker.System => "System",
            _ => line.Speaker.ToString(),
        };
        var suffix = line.IsInterim ? " (listening...)" : string.Empty;
        return $"[{line.Timestamp:HH:mm:ss}] {speakerLabel}: {line.Text}{suffix}";
    }

    private static Color GetTranscriptColor(TranscriptSpeaker speaker) => speaker switch
    {
        TranscriptSpeaker.Caller => Color.DarkBlue,
        TranscriptSpeaker.ServiceDesk => Color.DarkGreen,
        TranscriptSpeaker.System => Color.DarkRed,
        _ => SystemColors.WindowText,
    };

    private readonly List<int> _transcriptLineStarts = [];

    private void AppendTranscriptDisplayLine(string text, Color color)
    {
        _transcriptLineStarts.Add(TranscriptRichTextBox.TextLength);
        TranscriptRichTextBox.SelectionColor = color;
        TranscriptRichTextBox.AppendText(text + Environment.NewLine);
        TranscriptRichTextBox.SelectionColor = TranscriptRichTextBox.ForeColor;
        TranscriptRichTextBox.SelectionStart = TranscriptRichTextBox.TextLength;
        TranscriptRichTextBox.ScrollToCaret();
    }

    private void ReplaceTranscriptDisplayLine(int index, string text, Color color)
    {
        if (index < 0 || index >= _transcriptLineStarts.Count)
        {
            AppendTranscriptDisplayLine(text, color);
            return;
        }

        var start = _transcriptLineStarts[index];
        var end = index + 1 < _transcriptLineStarts.Count
            ? _transcriptLineStarts[index + 1]
            : TranscriptRichTextBox.TextLength;

        TranscriptRichTextBox.Select(start, end - start);
        TranscriptRichTextBox.SelectionColor = color;
        TranscriptRichTextBox.SelectedText = text + Environment.NewLine;
        TranscriptRichTextBox.SelectionColor = TranscriptRichTextBox.ForeColor;

        var delta = TranscriptRichTextBox.TextLength - end;
        for (var i = index + 1; i < _transcriptLineStarts.Count; i++)
        {
            _transcriptLineStarts[i] += delta;
        }

        TranscriptRichTextBox.SelectionStart = TranscriptRichTextBox.TextLength;
        TranscriptRichTextBox.ScrollToCaret();
    }

    private void AppendDiagnosticsMarshaled(string safeMessage) => MarshalToUi(() => AppendDiagnostics(safeMessage));

    private void AppendDiagnostics(string safeMessage)
    {
        DiagnosticsRichTextBox.AppendText($"[{DateTimeOffset.Now:HH:mm:ss}] {safeMessage}{Environment.NewLine}");
        DiagnosticsRichTextBox.SelectionStart = DiagnosticsRichTextBox.TextLength;
        DiagnosticsRichTextBox.ScrollToCaret();
    }

    /// <summary>
    /// Reduces an exception to a fixed, actionable message which cannot disclose credentials,
    /// authentication tokens, prompt/transcript content, or raw SDK/CLI diagnostic output.
    /// </summary>
    private static string SafeMessage(Exception exception) => exception switch
    {
        AzureAuthenticationTimeoutException timeout => timeout.Message,
        OperationCanceledException => "The operation was cancelled.",
        global::Azure.RequestFailedException => "The Azure service operation failed. Verify configuration and permissions.",
        global::Azure.Identity.AuthenticationFailedException =>
            "Azure authentication failed. Sign in with Azure CLI or verify the configured identity.",
        _ when exception.GetType().Namespace?.StartsWith("Azure", StringComparison.Ordinal) == true =>
            "The Azure service operation failed. Verify configuration and permissions.",
        InvalidOperationException => "The requested operation could not be completed. Review the configuration and retry.",
        _ => "An unexpected operation failed. Retry, then review the local application diagnostics.",
    };

    private Task RunOnUiAsync(Action action)
    {
        if (IsDisposed)
        {
            return Task.CompletedTask;
        }

        if (!InvokeRequired)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(new Action(() =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            }));
        }
        catch (ObjectDisposedException)
        {
            completion.SetResult();
        }
        catch (InvalidOperationException)
        {
            completion.SetResult();
        }

        return completion.Task;
    }

    private Task<TunnelCleanupResult> DisposeTunnelSessionAsync(DevTunnelSession? expectedSession = null)
    {
        lock (_tunnelCleanupSync)
        {
            var session = expectedSession ?? _tunnelSession;
            if (session is null)
            {
                return Task.FromResult(TunnelCleanupResult.Completed);
            }

            if (ReferenceEquals(_cleaningTunnelSession, session))
            {
                return _tunnelCleanupTask!;
            }

            if (ReferenceEquals(_tunnelSession, session))
            {
                _tunnelSession = null;
                Interlocked.Increment(ref _tunnelSessionGeneration);
            }

            _cleaningTunnelSession = session;
            _tunnelCleanupTask = DisposeTunnelSessionCoreAsync(session);
            return _tunnelCleanupTask;
        }
    }

    private async Task<TunnelCleanupResult> DisposeTunnelSessionCoreAsync(DevTunnelSession session)
    {
        var succeeded = false;
        try
        {
            session.CallbackHostStarted -= OnDevTunnelCallbackHostStarted;
            session.SignInRequired -= OnDevTunnelSignInRequired;
            await session.DisposeAsync().ConfigureAwait(false);
            succeeded = true;
        }
        catch (Exception exception)
        {
            AppendDiagnosticsMarshaled($"Callback/tunnel cleanup reported: {SafeMessage(exception)}");
        }
        finally
        {
            lock (_tunnelCleanupSync)
            {
                if (!succeeded && _tunnelSession is null)
                {
                    // Preserve the exact owned session for a later explicit retry. Replacing it
                    // with a new session here would lose the only safe deletion handle or an
                    // incompletely disposed callback host.
                    _tunnelSession = session;
                    Interlocked.Increment(ref _tunnelSessionGeneration);
                }

                if (ReferenceEquals(_cleaningTunnelSession, session))
                {
                    _cleaningTunnelSession = null;
                    _tunnelCleanupTask = null;
                }
            }
        }

        return new TunnelCleanupResult(succeeded, session.HasRetainedTunnel);
    }

    private bool IsTunnelSessionRetained(DevTunnelSession session)
    {
        lock (_tunnelCleanupSync)
        {
            return session.HasRetainedTunnel
                || ReferenceEquals(_tunnelSession, session)
                || ReferenceEquals(_cleaningTunnelSession, session);
        }
    }

    private DevTunnelSession? GetOwnedTunnelSession()
    {
        lock (_tunnelCleanupSync)
        {
            return _tunnelSession ?? _cleaningTunnelSession;
        }
    }

    private bool TryGetCurrentTunnelSession(
        out DevTunnelSession tunnelSession,
        out long tunnelSessionGeneration)
    {
        lock (_tunnelCleanupSync)
        {
            if (_tunnelSession is null)
            {
                tunnelSession = null!;
                tunnelSessionGeneration = 0;
                return false;
            }

            tunnelSession = _tunnelSession;
            tunnelSessionGeneration = Interlocked.Read(ref _tunnelSessionGeneration);
            return true;
        }
    }

    private void MarshalToUi(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(action);
            }
            catch (ObjectDisposedException)
            {
                // The window closed while a worker event was in flight; safe to ignore.
            }
            catch (InvalidOperationException)
            {
                // The window handle was torn down concurrently; safe to ignore.
            }
        }
        else
        {
            action();
        }
    }

    /// <summary>
    /// Groups the per-call resources this form owns and must dispose exactly once.
    /// </summary>
    private sealed class ActiveCallResources(
        AcsCallSession callSession,
        ScriptedCallerOrchestrator orchestrator,
        IAudioMonitor monitor,
        CallerScriptSnapshot script,
        CallEventRouter router,
        DevTunnelSession tunnelSession,
        long tunnelSessionGeneration)
    {
        public SimulatorCallResources<AcsCallSession> Resources { get; } =
            new(callSession, orchestrator, monitor, script);

        public AcsCallSession CallSession { get; } = callSession;

        public ScriptedCallerOrchestrator Orchestrator { get; } = orchestrator;

        public IAudioMonitor Monitor { get; } = monitor;

        public CallerScriptSnapshot Script { get; } = script;

        public CallEventRouter Router { get; } = router;

        public DevTunnelSession TunnelSession { get; } = tunnelSession;

        public long TunnelSessionGeneration { get; } = tunnelSessionGeneration;

        public Action<CallStateChange>? CallEndedHandler { get; set; }

        private readonly object _finalizeSync = new();
        private Task? _finalizeTask;

        public Task BeginFinalize(Func<Task> finalize)
        {
            lock (_finalizeSync)
            {
                return _finalizeTask ??= finalize();
            }
        }

        public bool UsesTunnelSession(DevTunnelSession session, long sessionGeneration) =>
            ReferenceEquals(TunnelSession, session) && TunnelSessionGeneration == sessionGeneration;
    }

    private readonly record struct TunnelCleanupResult(bool Succeeded, bool TunnelRetained)
    {
        public static TunnelCleanupResult Completed { get; } = new(true, false);
    }
}
