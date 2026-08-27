namespace ServiceDeskCallSimulator.UI;

partial class MainForm
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null!;

    /// <summary>
    ///  Clean up any resources being used.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
            DisposeCompositionResources();
        }

        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    // ---- Logical (96 DPI) layout metrics ------------------------------------------------
    // Every value below comes from ChromeLayoutMetrics, the single WinForms-independent source
    // of truth shared by the designer and the layout regression tests. WinForms scales Size,
    // MinimumSize and MaximumSize when the window moves to a higher-DPI monitor, so these bounds
    // stay correct at 100%, 150%, 200% and beyond.

    /// <summary>Explicit size of the primary command-bar buttons (Call / Hang Up).</summary>
    internal static Size CommandButtonLogicalSize => ChromeLayoutMetrics.CommandButtonLogicalSize;

    /// <summary>Hard upper bound for the primary command-bar buttons.</summary>
    internal static Size CommandButtonMaxLogicalSize => ChromeLayoutMetrics.CommandButtonMaxLogicalSize;

    /// <summary>Explicit size of the secondary inline buttons (Copy / Retry).</summary>
    internal static Size SecondaryButtonLogicalSize => ChromeLayoutMetrics.SecondaryButtonLogicalSize;

    /// <summary>Hard upper bound for the secondary inline buttons.</summary>
    internal static Size SecondaryButtonMaxLogicalSize => ChromeLayoutMetrics.SecondaryButtonMaxLogicalSize;

    /// <summary>Height cap for the whole status header.</summary>
    internal const int HeaderMaxLogicalHeight = ChromeLayoutMetrics.HeaderMaxLogicalHeight;

    /// <summary>Height cap for the whole bottom command bar.</summary>
    internal const int CommandBarMaxLogicalHeight = ChromeLayoutMetrics.CommandBarMaxLogicalHeight;

    private const int InitializationErrorMaxLogicalWidth = ChromeLayoutMetrics.InitializationErrorMaxLogicalWidth;
    private const int CallDisabledReasonMaxLogicalWidth = ChromeLayoutMetrics.CallDisabledReasonMaxLogicalWidth;

    /// <summary>
    /// Creates one compact, auto-sized row of the status header. Rows fill their header cell
    /// horizontally and contribute only their own preferred height.
    /// </summary>
    private static TableLayoutPanel CreateHeaderRow(string name)
    {
        var row = new TableLayoutPanel
        {
            Name = name,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            RowCount = 1,
        };
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return row;
    }

    /// <summary>
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        RootLayoutPanel = new TableLayoutPanel();

        StatusHeaderPanel = new TableLayoutPanel();
        StatusRow = new TableLayoutPanel();
        StatusIconLabel = new Label();
        StatusBannerLabel = new Label();
        ChecklistPanel = new TableLayoutPanel();
        AzureAuthChecklistLabel = new Label();
        NumberDiscoveryChecklistLabel = new Label();
        CallbackHostChecklistLabel = new Label();
        DevTunnelChecklistLabel = new Label();
        CallbackHostLabel = new Label();
        CallbackHostTextBox = new TextBox();
        CopyCallbackHostButton = new Button();
        SelectedModelLabel = new Label();
        SelectedModelTextBox = new TextBox();
        InitializationErrorLabel = new Label();
        RetryButton = new Button();

        WorkingAreaSplitContainer = new SplitContainer();

        RoutingGroupBox = new GroupBox();
        CallerIdLabel = new Label();
        CallerIdComboBox = new ComboBox();
        RefreshNumbersButton = new Button();
        DestinationLabel = new Label();
        DestinationTextBox = new TextBox();
        RoutingErrorProvider = new ErrorProvider(components);

        ScriptGroupBox = new GroupBox();
        PresetLabel = new Label();
        PresetComboBox = new ComboBox();
        LocaleCaptionLabel = new Label();
        LocaleValueLabel = new Label();
        VoiceCaptionLabel = new Label();
        VoiceValueLabel = new Label();
        ScriptFieldsPanel = new TableLayoutPanel();
        IdentityLabel = new Label();
        IdentityTextBox = new TextBox();
        BackgroundLabel = new Label();
        BackgroundTextBox = new TextBox();
        ReasonLabel = new Label();
        ReasonTextBox = new TextBox();
        UrgencyLabel = new Label();
        UrgencyTextBox = new TextBox();
        CallbackNumberLabel = new Label();
        CallbackNumberTextBox = new TextBox();
        AdditionalDetailsLabel = new Label();
        AdditionalDetailsTextBox = new TextBox();
        ResetPresetButton = new Button();

        SetupPanel = new Panel();

        LiveCallPanel = new Panel();
        SummaryPanel = new TableLayoutPanel();
        CallStateCaptionLabel = new Label();
        CallStateValueLabel = new Label();
        ElapsedCaptionLabel = new Label();
        ElapsedValueLabel = new Label();
        CallerIdCaptionLabel = new Label();
        CallerIdValueLabel = new Label();
        DestinationCaptionLabel = new Label();
        DestinationValueLabel = new Label();
        ActivityCaptionLabel = new Label();
        ActivityValueLabel = new Label();
        ConversationTabControl = new TabControl();
        TranscriptTabPage = new TabPage();
        TranscriptRichTextBox = new RichTextBox();
        DiagnosticsTabPage = new TabPage();
        DiagnosticsRichTextBox = new RichTextBox();
        ClearTranscriptButton = new Button();

        CommandBarPanel = new FlowLayoutPanel();
        CallDisabledReasonLabel = new Label();
        MuteLocalAudioCheckBox = new CheckBox();
        HangUpButton = new Button();
        CallButton = new Button();

        ((System.ComponentModel.ISupportInitialize)WorkingAreaSplitContainer).BeginInit();
        WorkingAreaSplitContainer.Panel1.SuspendLayout();
        WorkingAreaSplitContainer.Panel2.SuspendLayout();
        WorkingAreaSplitContainer.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)RoutingErrorProvider).BeginInit();
        RootLayoutPanel.SuspendLayout();
        StatusHeaderPanel.SuspendLayout();
        StatusRow.SuspendLayout();
        ChecklistPanel.SuspendLayout();
        RoutingGroupBox.SuspendLayout();
        ScriptGroupBox.SuspendLayout();
        ScriptFieldsPanel.SuspendLayout();
        SetupPanel.SuspendLayout();
        LiveCallPanel.SuspendLayout();
        SummaryPanel.SuspendLayout();
        ConversationTabControl.SuspendLayout();
        TranscriptTabPage.SuspendLayout();
        DiagnosticsTabPage.SuspendLayout();
        CommandBarPanel.SuspendLayout();
        SuspendLayout();

        // ---- Root layout: header (auto) / working area (100%) / command bar (auto) ----
        RootLayoutPanel.Name = "RootLayoutPanel";
        RootLayoutPanel.Dock = DockStyle.Fill;
        RootLayoutPanel.ColumnCount = 1;
        RootLayoutPanel.RowCount = 3;
        RootLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        RootLayoutPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RootLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        RootLayoutPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RootLayoutPanel.Controls.Add(StatusHeaderPanel, 0, 0);
        RootLayoutPanel.Controls.Add(WorkingAreaSplitContainer, 0, 1);
        RootLayoutPanel.Controls.Add(CommandBarPanel, 0, 2);

        // ---- Status header: a real row-based TableLayoutPanel ----------------------------
        // Five explicit AutoSize rows (status, checklist, callback host, selected model,
        // initialization error) instead of overlapping Dock=Top/Dock=Left children in a plain
        // Panel. Every child anchors instead of docking, so no row can be inflated by a child
        // stretching to fill the cell, and the whole header is height-capped so it can never
        // starve the working area on a high-DPI desktop.
        StatusHeaderPanel.Name = "StatusHeaderPanel";
        StatusHeaderPanel.Dock = DockStyle.Fill;
        StatusHeaderPanel.AutoSize = true;
        StatusHeaderPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StatusHeaderPanel.Margin = new Padding(0);
        StatusHeaderPanel.Padding = new Padding(8, 4, 8, 4);
        // A finite "unbounded" width: Control.MaximumSize's documented zero sentinel does not
        // survive DPI scaling and would collapse this bordered auto-sized header to 2 px.
        StatusHeaderPanel.MaximumSize = ChromeLayoutMetrics.MaxHeight(HeaderMaxLogicalHeight);
        StatusHeaderPanel.BorderStyle = BorderStyle.FixedSingle;
        StatusHeaderPanel.ColumnCount = 1;
        StatusHeaderPanel.RowCount = 5;
        StatusHeaderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        StatusHeaderPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // status
        StatusHeaderPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // checklist
        StatusHeaderPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // callback host
        StatusHeaderPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // selected model
        StatusHeaderPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // error + retry

        // Row 0: status indicator + status text.
        StatusIconLabel.Name = "StatusIconLabel";
        StatusIconLabel.AccessibleName = "Application status indicator";
        StatusIconLabel.AccessibleDescription = "Visual indicator that accompanies the application status text.";
        StatusIconLabel.AutoSize = true;
        StatusIconLabel.Anchor = AnchorStyles.Left;
        StatusIconLabel.Margin = new Padding(0, 0, 6, 0);
        StatusIconLabel.Font = new Font(Font!, FontStyle.Bold);
        StatusIconLabel.Text = "●";
        StatusIconLabel.TabStop = false;

        StatusBannerLabel.Name = "StatusBannerLabel";
        StatusBannerLabel.AccessibleName = "Application status";
        StatusBannerLabel.AccessibleDescription = "Shows the current application phase: Initializing, Sign-in required, Ready, Dialing, Connected, Ending, or Error.";
        StatusBannerLabel.AutoSize = true;
        StatusBannerLabel.Anchor = AnchorStyles.Left;
        StatusBannerLabel.Margin = new Padding(0, 0, 0, 0);
        StatusBannerLabel.Font = new Font(Font!, FontStyle.Bold);
        StatusBannerLabel.Text = "Initializing";
        StatusBannerLabel.TabIndex = 0;
        StatusBannerLabel.TabStop = false;

        StatusRow = CreateHeaderRow("StatusRow");
        StatusRow.ColumnCount = 2;
        StatusRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        StatusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        StatusRow.Controls.Add(StatusIconLabel, 0, 0);
        StatusRow.Controls.Add(StatusBannerLabel, 1, 0);

        // Row 1: the four initialization checklist items.
        ChecklistPanel.Name = "ChecklistPanel";
        ChecklistPanel.Dock = DockStyle.Fill;
        ChecklistPanel.AutoSize = true;
        ChecklistPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ChecklistPanel.Margin = new Padding(0);
        ChecklistPanel.ColumnCount = 4;
        ChecklistPanel.RowCount = 1;
        ChecklistPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        ChecklistPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        ChecklistPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        ChecklistPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        ChecklistPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        AzureAuthChecklistLabel.Name = "AzureAuthChecklistLabel";
        AzureAuthChecklistLabel.AccessibleName = "Azure authentication status";
        AzureAuthChecklistLabel.AutoSize = true;
        AzureAuthChecklistLabel.Anchor = AnchorStyles.Left;
        AzureAuthChecklistLabel.Margin = new Padding(0, 2, 8, 2);
        AzureAuthChecklistLabel.Text = "Azure authentication: Pending";
        AzureAuthChecklistLabel.TabStop = false;

        NumberDiscoveryChecklistLabel.Name = "NumberDiscoveryChecklistLabel";
        NumberDiscoveryChecklistLabel.AccessibleName = "ACS number discovery status";
        NumberDiscoveryChecklistLabel.AutoSize = true;
        NumberDiscoveryChecklistLabel.Anchor = AnchorStyles.Left;
        NumberDiscoveryChecklistLabel.Margin = new Padding(0, 2, 8, 2);
        NumberDiscoveryChecklistLabel.Text = "Number discovery: Pending";
        NumberDiscoveryChecklistLabel.TabStop = false;

        CallbackHostChecklistLabel.Name = "CallbackHostChecklistLabel";
        CallbackHostChecklistLabel.AccessibleName = "Local callback host status";
        CallbackHostChecklistLabel.AutoSize = true;
        CallbackHostChecklistLabel.Anchor = AnchorStyles.Left;
        CallbackHostChecklistLabel.Margin = new Padding(0, 2, 8, 2);
        CallbackHostChecklistLabel.Text = "Callback host: Pending";
        CallbackHostChecklistLabel.TabStop = false;

        DevTunnelChecklistLabel.Name = "DevTunnelChecklistLabel";
        DevTunnelChecklistLabel.AccessibleName = "Dev Tunnel status";
        DevTunnelChecklistLabel.AutoSize = true;
        DevTunnelChecklistLabel.Anchor = AnchorStyles.Left;
        DevTunnelChecklistLabel.Margin = new Padding(0, 2, 0, 2);
        DevTunnelChecklistLabel.Text = "Dev Tunnel: Pending";
        DevTunnelChecklistLabel.TabStop = false;

        ChecklistPanel.Controls.Add(AzureAuthChecklistLabel, 0, 0);
        ChecklistPanel.Controls.Add(NumberDiscoveryChecklistLabel, 1, 0);
        ChecklistPanel.Controls.Add(CallbackHostChecklistLabel, 2, 0);
        ChecklistPanel.Controls.Add(DevTunnelChecklistLabel, 3, 0);

        // Row 2: public callback host + Copy.
        CallbackHostLabel.Name = "CallbackHostLabel";
        CallbackHostLabel.AutoSize = true;
        CallbackHostLabel.Anchor = AnchorStyles.Left;
        CallbackHostLabel.Margin = new Padding(0, 2, 8, 2);
        CallbackHostLabel.Text = "Public callback host:";
        CallbackHostLabel.TabStop = false;

        CallbackHostTextBox.Name = "CallbackHostTextBox";
        CallbackHostTextBox.AccessibleName = "Public callback host";
        CallbackHostTextBox.AccessibleDescription = "The public Dev Tunnel host used for ACS callbacks.";
        CallbackHostTextBox.ReadOnly = true;
        CallbackHostTextBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        CallbackHostTextBox.Margin = new Padding(0, 2, 8, 2);
        CallbackHostTextBox.TabIndex = 1;

        CopyCallbackHostButton.Name = "CopyCallbackHostButton";
        CopyCallbackHostButton.AccessibleName = "Copy public callback host";
        CopyCallbackHostButton.AutoSize = false;
        CopyCallbackHostButton.Anchor = AnchorStyles.None;
        CopyCallbackHostButton.Size = SecondaryButtonLogicalSize;
        CopyCallbackHostButton.MinimumSize = SecondaryButtonLogicalSize;
        CopyCallbackHostButton.MaximumSize = SecondaryButtonMaxLogicalSize;
        CopyCallbackHostButton.Margin = new Padding(0, 2, 0, 2);
        CopyCallbackHostButton.Text = "Copy";
        CopyCallbackHostButton.TabIndex = 2;
        CopyCallbackHostButton.Click += OnCopyCallbackHostButtonClick;

        CallbackHostRow = CreateHeaderRow("CallbackHostRow");
        CallbackHostRow.ColumnCount = 3;
        CallbackHostRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        CallbackHostRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        CallbackHostRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        CallbackHostRow.Controls.Add(CallbackHostLabel, 0, 0);
        CallbackHostRow.Controls.Add(CallbackHostTextBox, 1, 0);
        CallbackHostRow.Controls.Add(CopyCallbackHostButton, 2, 0);

        // Row 3: selected model.
        SelectedModelLabel.Name = "SelectedModelLabel";
        SelectedModelLabel.AutoSize = true;
        SelectedModelLabel.Anchor = AnchorStyles.Left;
        SelectedModelLabel.Margin = new Padding(0, 2, 8, 2);
        SelectedModelLabel.Text = "Selected model:";
        SelectedModelLabel.TabStop = false;

        SelectedModelTextBox.Name = "SelectedModelTextBox";
        SelectedModelTextBox.AccessibleName = "Selected model";
        SelectedModelTextBox.ReadOnly = true;
        SelectedModelTextBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        SelectedModelTextBox.Margin = new Padding(0, 2, 0, 2);
        SelectedModelTextBox.TabIndex = 3;

        SelectedModelRow = CreateHeaderRow("SelectedModelRow");
        SelectedModelRow.ColumnCount = 2;
        SelectedModelRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        SelectedModelRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        SelectedModelRow.Controls.Add(SelectedModelLabel, 0, 0);
        SelectedModelRow.Controls.Add(SelectedModelTextBox, 1, 0);

        // Row 4: inline initialization error + Retry. Both stay hidden until a stage fails, so
        // this row contributes no height in the healthy case.
        InitializationErrorLabel.Name = "InitializationErrorLabel";
        InitializationErrorLabel.AccessibleName = "Initialization error";
        InitializationErrorLabel.AutoSize = true;
        InitializationErrorLabel.Anchor = AnchorStyles.Left;
        InitializationErrorLabel.Margin = new Padding(0, 2, 8, 2);
        InitializationErrorLabel.MaximumSize = ChromeLayoutMetrics.MaxWidth(InitializationErrorMaxLogicalWidth);
        InitializationErrorLabel.ForeColor = Color.DarkRed;
        InitializationErrorLabel.Visible = false;
        InitializationErrorLabel.TabStop = false;

        RetryButton.Name = "RetryButton";
        RetryButton.AccessibleName = "Retry initialization";
        RetryButton.AutoSize = false;
        RetryButton.Anchor = AnchorStyles.None;
        RetryButton.Size = SecondaryButtonLogicalSize;
        RetryButton.MinimumSize = SecondaryButtonLogicalSize;
        RetryButton.MaximumSize = SecondaryButtonMaxLogicalSize;
        RetryButton.Margin = new Padding(0, 2, 0, 2);
        RetryButton.Text = "&Retry";
        RetryButton.Visible = false;
        RetryButton.TabIndex = 4;
        RetryButton.Click += OnRetryButtonClick;

        InitializationErrorRow = CreateHeaderRow("InitializationErrorRow");
        InitializationErrorRow.ColumnCount = 2;
        InitializationErrorRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        InitializationErrorRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        InitializationErrorRow.Controls.Add(InitializationErrorLabel, 0, 0);
        InitializationErrorRow.Controls.Add(RetryButton, 1, 0);

        StatusHeaderPanel.Controls.Add(StatusRow, 0, 0);
        StatusHeaderPanel.Controls.Add(ChecklistPanel, 0, 1);
        StatusHeaderPanel.Controls.Add(CallbackHostRow, 0, 2);
        StatusHeaderPanel.Controls.Add(SelectedModelRow, 0, 3);
        StatusHeaderPanel.Controls.Add(InitializationErrorRow, 0, 4);

        // ---- Working area: SplitContainer -----------------------------------------------
        WorkingAreaSplitContainer.Name = "WorkingAreaSplitContainer";
        WorkingAreaSplitContainer.Size = new Size(1200, 700);
        WorkingAreaSplitContainer.Margin = new Padding(0);
        WorkingAreaSplitContainer.FixedPanel = FixedPanel.Panel1;
        WorkingAreaSplitContainer.Panel1MinSize = 280;
        WorkingAreaSplitContainer.Panel2MinSize = 320;
        WorkingAreaSplitContainer.SplitterDistance = 340;
        WorkingAreaSplitContainer.Dock = DockStyle.Fill;
        WorkingAreaSplitContainer.TabIndex = 5;

        // ---- Left setup pane: Routing + Caller script ------------------------------------
        SetupPanel.Name = "SetupPanel";
        SetupPanel.Dock = DockStyle.Fill;
        SetupPanel.AutoScroll = true;

        RoutingGroupBox.Name = "RoutingGroupBox";
        RoutingGroupBox.AccessibleName = "Routing";
        RoutingGroupBox.Text = "Routing";
        RoutingGroupBox.Dock = DockStyle.Top;
        RoutingGroupBox.AutoSize = true;
        RoutingGroupBox.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        RoutingGroupBox.Padding = new Padding(8);
        RoutingGroupBox.TabIndex = 0;

        CallerIdLabel.Name = "CallerIdLabel";
        CallerIdLabel.AutoSize = true;
        CallerIdLabel.Text = "Caller ID:";
        CallerIdLabel.TabStop = false;

        CallerIdComboBox.Name = "CallerIdComboBox";
        CallerIdComboBox.AccessibleName = "Caller ID";
        CallerIdComboBox.AccessibleDescription = "The outbound-capable ACS number used as the caller ID.";
        CallerIdComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        CallerIdComboBox.Dock = DockStyle.Fill;
        CallerIdComboBox.TabIndex = 1;
        CallerIdComboBox.SelectedIndexChanged += OnCallerIdComboBoxSelectedIndexChanged;

        RefreshNumbersButton.Name = "RefreshNumbersButton";
        RefreshNumbersButton.AccessibleName = "Refresh caller IDs";
        RefreshNumbersButton.AutoSize = true;
        RefreshNumbersButton.Text = "Re&fresh";
        RefreshNumbersButton.TabIndex = 2;
        RefreshNumbersButton.Click += OnRefreshNumbersButtonClick;

        var callerIdRow = new TableLayoutPanel
        {
            Name = "CallerIdRow",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
        };
        callerIdRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        callerIdRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        callerIdRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        callerIdRow.Controls.Add(CallerIdLabel, 0, 0);
        callerIdRow.Controls.Add(CallerIdComboBox, 1, 0);
        callerIdRow.Controls.Add(RefreshNumbersButton, 2, 0);
        CallerIdRow = callerIdRow;

        DestinationLabel.Name = "DestinationLabel";
        DestinationLabel.AutoSize = true;
        DestinationLabel.Text = "Destination:";
        DestinationLabel.TabStop = false;

        DestinationTextBox.Name = "DestinationTextBox";
        DestinationTextBox.AccessibleName = "Destination phone number";
        DestinationTextBox.AccessibleDescription = "The E.164 destination number to dial.";
        DestinationTextBox.Dock = DockStyle.Fill;
        DestinationTextBox.TabIndex = 3;
        DestinationTextBox.TextChanged += OnDestinationTextBoxTextChanged;

        var destinationRow = new TableLayoutPanel
        {
            Name = "DestinationRow",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
        };
        destinationRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        destinationRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        destinationRow.Controls.Add(DestinationLabel, 0, 0);
        destinationRow.Controls.Add(DestinationTextBox, 1, 0);
        DestinationRow = destinationRow;

        RoutingErrorProvider.ContainerControl = this;

        RoutingGroupBox.Controls.Add(destinationRow);
        RoutingGroupBox.Controls.Add(callerIdRow);

        ScriptGroupBox.Name = "ScriptGroupBox";
        ScriptGroupBox.AccessibleName = "Caller script";
        ScriptGroupBox.Text = "Caller script";
        ScriptGroupBox.Dock = DockStyle.Fill;
        ScriptGroupBox.Padding = new Padding(8);
        ScriptGroupBox.TabIndex = 1;

        PresetLabel.Name = "PresetLabel";
        PresetLabel.AutoSize = true;
        PresetLabel.Text = "Preset:";
        PresetLabel.TabStop = false;

        PresetComboBox.Name = "PresetComboBox";
        PresetComboBox.AccessibleName = "Caller script preset";
        PresetComboBox.AccessibleDescription = "Selects one of the eight built-in caller script presets.";
        PresetComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        PresetComboBox.Dock = DockStyle.Fill;
        PresetComboBox.TabIndex = 4;
        PresetComboBox.SelectedIndexChanged += OnPresetComboBoxSelectedIndexChanged;

        var presetRow = new TableLayoutPanel
        {
            Name = "PresetRow",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
        };
        presetRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        presetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        presetRow.Controls.Add(PresetLabel, 0, 0);
        presetRow.Controls.Add(PresetComboBox, 1, 0);
        PresetRow = presetRow;

        LocaleCaptionLabel.Name = "LocaleCaptionLabel";
        LocaleCaptionLabel.AutoSize = true;
        LocaleCaptionLabel.Text = "Locale:";
        LocaleCaptionLabel.TabStop = false;

        LocaleValueLabel.Name = "LocaleValueLabel";
        LocaleValueLabel.AccessibleName = "Preset locale";
        LocaleValueLabel.AutoSize = true;
        LocaleValueLabel.Dock = DockStyle.Fill;
        LocaleValueLabel.TabStop = false;

        VoiceCaptionLabel.Name = "VoiceCaptionLabel";
        VoiceCaptionLabel.AutoSize = true;
        VoiceCaptionLabel.Text = "Voice:";
        VoiceCaptionLabel.TabStop = false;

        VoiceValueLabel.Name = "VoiceValueLabel";
        VoiceValueLabel.AccessibleName = "Preset voice";
        VoiceValueLabel.AutoSize = true;
        VoiceValueLabel.Dock = DockStyle.Fill;
        VoiceValueLabel.TabStop = false;

        var localeVoiceRow = new TableLayoutPanel
        {
            Name = "LocaleVoiceRow",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 1,
        };
        localeVoiceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        localeVoiceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        localeVoiceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        localeVoiceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        localeVoiceRow.Controls.Add(LocaleCaptionLabel, 0, 0);
        localeVoiceRow.Controls.Add(LocaleValueLabel, 1, 0);
        localeVoiceRow.Controls.Add(VoiceCaptionLabel, 2, 0);
        localeVoiceRow.Controls.Add(VoiceValueLabel, 3, 0);
        LocaleVoiceRow = localeVoiceRow;

        ResetPresetButton.Name = "ResetPresetButton";
        ResetPresetButton.AccessibleName = "Reset to preset";
        ResetPresetButton.AutoSize = true;
        ResetPresetButton.Dock = DockStyle.Bottom;
        ResetPresetButton.Text = "&Reset to Preset";
        ResetPresetButton.TabIndex = 6;
        ResetPresetButton.Click += OnResetPresetButtonClick;

        BuildScriptFieldsPanel();

        ScriptGroupBox.Controls.Add(ScriptFieldsPanel);
        ScriptGroupBox.Controls.Add(ResetPresetButton);
        ScriptGroupBox.Controls.Add(localeVoiceRow);
        ScriptGroupBox.Controls.Add(presetRow);

        SetupPanel.Controls.Add(ScriptGroupBox);
        SetupPanel.Controls.Add(RoutingGroupBox);

        WorkingAreaSplitContainer.Panel1.Controls.Add(SetupPanel);
        WorkingAreaSplitContainer.Panel1.AccessibleName = "Setup pane";

        // ---- Right live-call pane ---------------------------------------------------------
        LiveCallPanel.Name = "LiveCallPanel";
        LiveCallPanel.Dock = DockStyle.Fill;

        SummaryPanel.Name = "SummaryPanel";
        SummaryPanel.Dock = DockStyle.Top;
        SummaryPanel.AutoSize = true;
        SummaryPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        SummaryPanel.ColumnCount = 10;
        SummaryPanel.RowCount = 1;
        for (var i = 0; i < 10; i++)
        {
            SummaryPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }

        CallStateCaptionLabel.Name = "CallStateCaptionLabel";
        CallStateCaptionLabel.AutoSize = true;
        CallStateCaptionLabel.Text = "Call:";
        CallStateCaptionLabel.TabStop = false;
        CallStateValueLabel.Name = "CallStateValueLabel";
        CallStateValueLabel.AccessibleName = "Call state";
        CallStateValueLabel.AutoSize = true;
        CallStateValueLabel.Text = "-";
        CallStateValueLabel.TabStop = false;

        ElapsedCaptionLabel.Name = "ElapsedCaptionLabel";
        ElapsedCaptionLabel.AutoSize = true;
        ElapsedCaptionLabel.Text = "Elapsed:";
        ElapsedCaptionLabel.TabStop = false;
        ElapsedValueLabel.Name = "ElapsedValueLabel";
        ElapsedValueLabel.AccessibleName = "Elapsed duration";
        ElapsedValueLabel.AutoSize = true;
        ElapsedValueLabel.Text = "00:00";
        ElapsedValueLabel.TabStop = false;

        CallerIdCaptionLabel.Name = "CallerIdCaptionLabel";
        CallerIdCaptionLabel.AutoSize = true;
        CallerIdCaptionLabel.Text = "Caller ID:";
        CallerIdCaptionLabel.TabStop = false;
        CallerIdValueLabel.Name = "CallerIdValueLabel";
        CallerIdValueLabel.AccessibleName = "Active caller ID";
        CallerIdValueLabel.AutoSize = true;
        CallerIdValueLabel.Text = "-";
        CallerIdValueLabel.TabStop = false;

        DestinationCaptionLabel.Name = "DestinationCaptionLabel";
        DestinationCaptionLabel.AutoSize = true;
        DestinationCaptionLabel.Text = "Destination:";
        DestinationCaptionLabel.TabStop = false;
        DestinationValueLabel.Name = "DestinationValueLabel";
        DestinationValueLabel.AccessibleName = "Active destination";
        DestinationValueLabel.AutoSize = true;
        DestinationValueLabel.Text = "-";
        DestinationValueLabel.TabStop = false;

        ActivityCaptionLabel.Name = "ActivityCaptionLabel";
        ActivityCaptionLabel.AutoSize = true;
        ActivityCaptionLabel.Text = "Activity:";
        ActivityCaptionLabel.TabStop = false;
        ActivityValueLabel.Name = "ActivityValueLabel";
        ActivityValueLabel.AccessibleName = "Caller activity";
        ActivityValueLabel.AutoSize = true;
        ActivityValueLabel.Text = "-";
        ActivityValueLabel.TabStop = false;

        SummaryPanel.Controls.Add(CallStateCaptionLabel, 0, 0);
        SummaryPanel.Controls.Add(CallStateValueLabel, 1, 0);
        SummaryPanel.Controls.Add(ElapsedCaptionLabel, 2, 0);
        SummaryPanel.Controls.Add(ElapsedValueLabel, 3, 0);
        SummaryPanel.Controls.Add(CallerIdCaptionLabel, 4, 0);
        SummaryPanel.Controls.Add(CallerIdValueLabel, 5, 0);
        SummaryPanel.Controls.Add(DestinationCaptionLabel, 6, 0);
        SummaryPanel.Controls.Add(DestinationValueLabel, 7, 0);
        SummaryPanel.Controls.Add(ActivityCaptionLabel, 8, 0);
        SummaryPanel.Controls.Add(ActivityValueLabel, 9, 0);

        TranscriptRichTextBox.Name = "TranscriptRichTextBox";
        TranscriptRichTextBox.AccessibleName = "Call transcript";
        TranscriptRichTextBox.AccessibleDescription = "Timestamped caller, service desk, and system transcript entries.";
        TranscriptRichTextBox.ReadOnly = true;
        TranscriptRichTextBox.Dock = DockStyle.Fill;
        TranscriptRichTextBox.TabIndex = 0;
        TranscriptTabPage.Controls.Add(TranscriptRichTextBox);
        TranscriptTabPage.Name = "TranscriptTabPage";
        TranscriptTabPage.Text = "Transcript";
        TranscriptTabPage.AccessibleName = "Transcript tab";

        DiagnosticsRichTextBox.Name = "DiagnosticsRichTextBox";
        DiagnosticsRichTextBox.AccessibleName = "Diagnostics";
        DiagnosticsRichTextBox.AccessibleDescription = "Safe lifecycle status and error messages. Never contains prompts, transcript content, audio, or credentials.";
        DiagnosticsRichTextBox.ReadOnly = true;
        DiagnosticsRichTextBox.Dock = DockStyle.Fill;
        DiagnosticsRichTextBox.TabIndex = 0;
        DiagnosticsTabPage.Controls.Add(DiagnosticsRichTextBox);
        DiagnosticsTabPage.Name = "DiagnosticsTabPage";
        DiagnosticsTabPage.Text = "Diagnostics";
        DiagnosticsTabPage.AccessibleName = "Diagnostics tab";

        ConversationTabControl.Name = "ConversationTabControl";
        ConversationTabControl.Dock = DockStyle.Fill;
        ConversationTabControl.TabIndex = 1;
        ConversationTabControl.TabPages.Add(TranscriptTabPage);
        ConversationTabControl.TabPages.Add(DiagnosticsTabPage);

        ClearTranscriptButton.Name = "ClearTranscriptButton";
        ClearTranscriptButton.AccessibleName = "Clear transcript";
        ClearTranscriptButton.AutoSize = true;
        ClearTranscriptButton.Dock = DockStyle.Bottom;
        ClearTranscriptButton.Text = "C&lear Transcript";
        ClearTranscriptButton.TabIndex = 2;
        ClearTranscriptButton.Click += OnClearTranscriptButtonClick;

        LiveCallPanel.Controls.Add(ConversationTabControl);
        LiveCallPanel.Controls.Add(ClearTranscriptButton);
        LiveCallPanel.Controls.Add(SummaryPanel);

        WorkingAreaSplitContainer.Panel2.Controls.Add(LiveCallPanel);
        WorkingAreaSplitContainer.Panel2.AccessibleName = "Live call pane";

        // ---- Command bar: bottom, right-aligned, compact and DPI-safe --------------------
        // Buttons carry explicit logical sizes plus max bounds and Anchor.None, so neither the
        // flow layout nor DPI scaling can stretch them into oversized blank rectangles, and the
        // whole bar is height-capped so it can never starve the working area.
        CommandBarPanel.Name = "CommandBarPanel";
        CommandBarPanel.Dock = DockStyle.Bottom;
        CommandBarPanel.AutoSize = true;
        CommandBarPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        CommandBarPanel.FlowDirection = FlowDirection.RightToLeft;
        CommandBarPanel.WrapContents = false;
        CommandBarPanel.Margin = new Padding(0);
        CommandBarPanel.Padding = new Padding(8, 4, 8, 4);
        CommandBarPanel.MaximumSize = ChromeLayoutMetrics.MaxHeight(CommandBarMaxLogicalHeight);

        CallButton.Name = "CallButton";
        CallButton.AccessibleName = "Call";
        CallButton.AccessibleDescription = "Starts an outbound call using the selected caller ID, destination, and caller script.";
        CallButton.AutoSize = false;
        CallButton.Anchor = AnchorStyles.None;
        CallButton.Size = CommandButtonLogicalSize;
        CallButton.MinimumSize = CommandButtonLogicalSize;
        CallButton.MaximumSize = CommandButtonMaxLogicalSize;
        CallButton.Margin = new Padding(8, 2, 0, 2);
        CallButton.Text = "&Call";
        CallButton.TabIndex = 0;
        CallButton.Click += OnCallButtonClick;

        HangUpButton.Name = "HangUpButton";
        HangUpButton.AccessibleName = "Hang up";
        HangUpButton.AccessibleDescription = "Ends the active call.";
        HangUpButton.AutoSize = false;
        HangUpButton.Anchor = AnchorStyles.None;
        HangUpButton.Size = CommandButtonLogicalSize;
        HangUpButton.MinimumSize = CommandButtonLogicalSize;
        HangUpButton.MaximumSize = CommandButtonMaxLogicalSize;
        HangUpButton.Margin = new Padding(8, 2, 0, 2);
        HangUpButton.Text = "&Hang Up";
        HangUpButton.Enabled = false;
        HangUpButton.TabIndex = 1;
        HangUpButton.Click += OnHangUpButtonClick;

        MuteLocalAudioCheckBox.Name = "MuteLocalAudioCheckBox";
        MuteLocalAudioCheckBox.AccessibleName = "Mute local audio";
        MuteLocalAudioCheckBox.AccessibleDescription = "Mutes only the local speaker monitor; the ACS call audio is unaffected.";
        MuteLocalAudioCheckBox.AutoSize = true;
        MuteLocalAudioCheckBox.Anchor = AnchorStyles.None;
        MuteLocalAudioCheckBox.Margin = new Padding(8, 2, 0, 2);
        MuteLocalAudioCheckBox.Text = "Mute local audio";
        MuteLocalAudioCheckBox.TabIndex = 2;
        MuteLocalAudioCheckBox.CheckedChanged += OnMuteLocalAudioCheckBoxCheckedChanged;

        CallDisabledReasonLabel.Name = "CallDisabledReasonLabel";
        CallDisabledReasonLabel.AccessibleName = "Call disabled reason";
        CallDisabledReasonLabel.AutoSize = true;
        CallDisabledReasonLabel.Anchor = AnchorStyles.None;
        CallDisabledReasonLabel.MaximumSize = ChromeLayoutMetrics.MaxWidth(CallDisabledReasonMaxLogicalWidth);
        CallDisabledReasonLabel.Margin = new Padding(8, 2, 16, 2);
        CallDisabledReasonLabel.TabStop = false;

        // FlowDirection.RightToLeft: add in visual right-to-left order.
        CommandBarPanel.Controls.Add(CallButton);
        CommandBarPanel.Controls.Add(HangUpButton);
        CommandBarPanel.Controls.Add(MuteLocalAudioCheckBox);
        CommandBarPanel.Controls.Add(CallDisabledReasonLabel);

        // ---- Form ---------------------------------------------------------------------------
        // AutoScaleMode.Dpi measures AutoScaleDimensions in DPI, not in font units. The design
        // baseline is therefore 96x96 DPI; using a font size such as (7, 15) here would make the
        // runtime scale factor 96/7 x 96/15 and inflate every explicitly sized control.
        AutoScaleDimensions = new SizeF(ChromeLayoutMetrics.DesignDpi, ChromeLayoutMetrics.DesignDpi);
        AutoScaleMode = AutoScaleMode.Dpi;

        // Logical (96 DPI) sizes. WinForms scales both when the window moves to a higher-DPI
        // monitor, so at 200% this window opens at 2320x1520 and can shrink to 1920x1360 - it
        // still fits comfortably on a high-DPI 5K x 2K desktop without forced maximization.
        ClientSize = new Size(
            ChromeLayoutMetrics.InitialLogicalClientWidth,
            ChromeLayoutMetrics.InitialLogicalClientHeight);
        MinimumSize = new Size(
            ChromeLayoutMetrics.MinimumLogicalWindowWidth,
            ChromeLayoutMetrics.MinimumLogicalWindowHeight);
        Controls.Add(RootLayoutPanel);
        Name = "MainForm";
        Text = "Service Desk Call Simulator";
        AccessibleName = "Service Desk Call Simulator";
        StartPosition = FormStartPosition.CenterScreen;

        ((System.ComponentModel.ISupportInitialize)WorkingAreaSplitContainer).EndInit();
        WorkingAreaSplitContainer.Panel1.ResumeLayout(false);
        WorkingAreaSplitContainer.Panel2.ResumeLayout(false);
        WorkingAreaSplitContainer.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)RoutingErrorProvider).EndInit();
        ScriptFieldsPanel.ResumeLayout(false);
        ScriptFieldsPanel.PerformLayout();
        ScriptGroupBox.ResumeLayout(false);
        ScriptGroupBox.PerformLayout();
        RoutingGroupBox.ResumeLayout(false);
        RoutingGroupBox.PerformLayout();
        SetupPanel.ResumeLayout(false);
        SummaryPanel.ResumeLayout(false);
        SummaryPanel.PerformLayout();
        TranscriptTabPage.ResumeLayout(false);
        DiagnosticsTabPage.ResumeLayout(false);
        ConversationTabControl.ResumeLayout(false);
        LiveCallPanel.ResumeLayout(false);
        LiveCallPanel.PerformLayout();
        CommandBarPanel.ResumeLayout(false);
        CommandBarPanel.PerformLayout();
        ChecklistPanel.ResumeLayout(false);
        ChecklistPanel.PerformLayout();
        StatusHeaderPanel.ResumeLayout(false);
        StatusHeaderPanel.PerformLayout();
        StatusRow.ResumeLayout(false);
        StatusRow.PerformLayout();
        RootLayoutPanel.ResumeLayout(false);
        RootLayoutPanel.PerformLayout();
        ResumeLayout(false);
    }

    private void BuildScriptFieldsPanel()
    {
        ScriptFieldsPanel.Name = "ScriptFieldsPanel";
        ScriptFieldsPanel.Dock = DockStyle.Fill;
        ScriptFieldsPanel.AutoScroll = true;
        ScriptFieldsPanel.ColumnCount = 2;
        ScriptFieldsPanel.RowCount = 5;
        ScriptFieldsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ScriptFieldsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var i = 0; i < 5; i++)
        {
            ScriptFieldsPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        IdentityLabel.Name = "IdentityLabel";
        IdentityLabel.AutoSize = true;
        IdentityLabel.Text = "Identity:";
        IdentityLabel.TabStop = false;
        IdentityTextBox.Name = "IdentityTextBox";
        IdentityTextBox.AccessibleName = "Caller identity";
        IdentityTextBox.Dock = DockStyle.Fill;
        IdentityTextBox.TabIndex = 5;
        IdentityTextBox.TextChanged += OnScriptFieldTextChanged;

        BackgroundLabel.Name = "BackgroundLabel";
        BackgroundLabel.AutoSize = true;
        BackgroundLabel.Text = "Background:";
        BackgroundLabel.TabStop = false;
        BackgroundTextBox.Name = "BackgroundTextBox";
        BackgroundTextBox.AccessibleName = "Background";
        BackgroundTextBox.Multiline = true;
        BackgroundTextBox.Height = 48;
        BackgroundTextBox.Dock = DockStyle.Fill;
        BackgroundTextBox.TabIndex = 6;
        BackgroundTextBox.TextChanged += OnScriptFieldTextChanged;

        ReasonLabel.Name = "ReasonLabel";
        ReasonLabel.AutoSize = true;
        ReasonLabel.Text = "Reason:";
        ReasonLabel.TabStop = false;
        ReasonTextBox.Name = "ReasonTextBox";
        ReasonTextBox.AccessibleName = "Reason";
        ReasonTextBox.Multiline = true;
        ReasonTextBox.Height = 48;
        ReasonTextBox.Dock = DockStyle.Fill;
        ReasonTextBox.TabIndex = 7;
        ReasonTextBox.TextChanged += OnScriptFieldTextChanged;

        UrgencyLabel.Name = "UrgencyLabel";
        UrgencyLabel.AutoSize = true;
        UrgencyLabel.Text = "Urgency:";
        UrgencyLabel.TabStop = false;
        UrgencyTextBox.Name = "UrgencyTextBox";
        UrgencyTextBox.AccessibleName = "Urgency";
        UrgencyTextBox.Dock = DockStyle.Fill;
        UrgencyTextBox.TabIndex = 8;
        UrgencyTextBox.TextChanged += OnScriptFieldTextChanged;

        CallbackNumberLabel.Name = "CallbackNumberLabel";
        CallbackNumberLabel.AutoSize = true;
        CallbackNumberLabel.Text = "Callback number:";
        CallbackNumberLabel.TabStop = false;
        CallbackNumberTextBox.Name = "CallbackNumberTextBox";
        CallbackNumberTextBox.AccessibleName = "Callback number";
        CallbackNumberTextBox.AccessibleDescription = "The E.164 callback number quoted by the caller script.";
        CallbackNumberTextBox.Dock = DockStyle.Fill;
        CallbackNumberTextBox.TabIndex = 9;
        CallbackNumberTextBox.TextChanged += OnScriptFieldTextChanged;

        AdditionalDetailsLabel.Name = "AdditionalDetailsLabel";
        AdditionalDetailsLabel.AutoSize = true;
        AdditionalDetailsLabel.Text = "Additional details:";
        AdditionalDetailsLabel.TabStop = false;
        AdditionalDetailsTextBox.Name = "AdditionalDetailsTextBox";
        AdditionalDetailsTextBox.AccessibleName = "Additional details";
        AdditionalDetailsTextBox.Multiline = true;
        AdditionalDetailsTextBox.Height = 48;
        AdditionalDetailsTextBox.Dock = DockStyle.Fill;
        AdditionalDetailsTextBox.TabIndex = 10;
        AdditionalDetailsTextBox.TextChanged += OnScriptFieldTextChanged;

        ScriptFieldsPanel.Controls.Add(IdentityLabel, 0, 0);
        ScriptFieldsPanel.Controls.Add(IdentityTextBox, 1, 0);
        ScriptFieldsPanel.Controls.Add(BackgroundLabel, 0, 1);
        ScriptFieldsPanel.Controls.Add(BackgroundTextBox, 1, 1);
        ScriptFieldsPanel.Controls.Add(ReasonLabel, 0, 2);
        ScriptFieldsPanel.Controls.Add(ReasonTextBox, 1, 2);
        ScriptFieldsPanel.Controls.Add(UrgencyLabel, 0, 3);
        ScriptFieldsPanel.Controls.Add(UrgencyTextBox, 1, 3);
        ScriptFieldsPanel.Controls.Add(CallbackNumberLabel, 0, 4);
        ScriptFieldsPanel.Controls.Add(CallbackNumberTextBox, 1, 4);

        // Additional details spans an extra row appended after the fixed rows above.
        ScriptFieldsPanel.RowCount = 6;
        ScriptFieldsPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        ScriptFieldsPanel.Controls.Add(AdditionalDetailsLabel, 0, 5);
        ScriptFieldsPanel.Controls.Add(AdditionalDetailsTextBox, 1, 5);
    }

    #endregion

    internal TableLayoutPanel RootLayoutPanel = null!;

    internal TableLayoutPanel StatusHeaderPanel = null!;
    internal TableLayoutPanel StatusRow = null!;
    internal Label StatusIconLabel = null!;
    internal Label StatusBannerLabel = null!;
    internal TableLayoutPanel ChecklistPanel = null!;
    internal Label AzureAuthChecklistLabel = null!;
    internal Label NumberDiscoveryChecklistLabel = null!;
    internal Label CallbackHostChecklistLabel = null!;
    internal Label DevTunnelChecklistLabel = null!;
    internal TableLayoutPanel CallbackHostRow = null!;
    private Label CallbackHostLabel = null!;
    internal TextBox CallbackHostTextBox = null!;
    internal Button CopyCallbackHostButton = null!;
    internal TableLayoutPanel SelectedModelRow = null!;
    private Label SelectedModelLabel = null!;
    internal TextBox SelectedModelTextBox = null!;
    internal TableLayoutPanel InitializationErrorRow = null!;
    internal Label InitializationErrorLabel = null!;
    internal Button RetryButton = null!;

    internal SplitContainer WorkingAreaSplitContainer = null!;

    private Panel SetupPanel = null!;
    internal GroupBox RoutingGroupBox = null!;
    private TableLayoutPanel CallerIdRow = null!;
    private Label CallerIdLabel = null!;
    internal ComboBox CallerIdComboBox = null!;
    internal Button RefreshNumbersButton = null!;
    private TableLayoutPanel DestinationRow = null!;
    private Label DestinationLabel = null!;
    internal TextBox DestinationTextBox = null!;
    internal ErrorProvider RoutingErrorProvider = null!;

    internal GroupBox ScriptGroupBox = null!;
    private TableLayoutPanel PresetRow = null!;
    private Label PresetLabel = null!;
    internal ComboBox PresetComboBox = null!;
    private TableLayoutPanel LocaleVoiceRow = null!;
    private Label LocaleCaptionLabel = null!;
    internal Label LocaleValueLabel = null!;
    private Label VoiceCaptionLabel = null!;
    internal Label VoiceValueLabel = null!;
    internal TableLayoutPanel ScriptFieldsPanel = null!;
    private Label IdentityLabel = null!;
    internal TextBox IdentityTextBox = null!;
    private Label BackgroundLabel = null!;
    internal TextBox BackgroundTextBox = null!;
    private Label ReasonLabel = null!;
    internal TextBox ReasonTextBox = null!;
    private Label UrgencyLabel = null!;
    internal TextBox UrgencyTextBox = null!;
    private Label CallbackNumberLabel = null!;
    internal TextBox CallbackNumberTextBox = null!;
    private Label AdditionalDetailsLabel = null!;
    internal TextBox AdditionalDetailsTextBox = null!;
    internal Button ResetPresetButton = null!;

    private Panel LiveCallPanel = null!;
    internal TableLayoutPanel SummaryPanel = null!;
    private Label CallStateCaptionLabel = null!;
    internal Label CallStateValueLabel = null!;
    private Label ElapsedCaptionLabel = null!;
    internal Label ElapsedValueLabel = null!;
    private Label CallerIdCaptionLabel = null!;
    internal Label CallerIdValueLabel = null!;
    private Label DestinationCaptionLabel = null!;
    internal Label DestinationValueLabel = null!;
    private Label ActivityCaptionLabel = null!;
    internal Label ActivityValueLabel = null!;
    internal TabControl ConversationTabControl = null!;
    private TabPage TranscriptTabPage = null!;
    internal RichTextBox TranscriptRichTextBox = null!;
    private TabPage DiagnosticsTabPage = null!;
    internal RichTextBox DiagnosticsRichTextBox = null!;
    internal Button ClearTranscriptButton = null!;

    internal FlowLayoutPanel CommandBarPanel = null!;
    internal Label CallDisabledReasonLabel = null!;
    internal CheckBox MuteLocalAudioCheckBox = null!;
    internal Button HangUpButton = null!;
    internal Button CallButton = null!;
}
