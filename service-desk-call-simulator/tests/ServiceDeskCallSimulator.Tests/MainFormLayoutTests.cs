using Microsoft.Extensions.DependencyInjection;
using ServiceDeskCallSimulator.Azure;
using ServiceDeskCallSimulator.Configuration;
using ServiceDeskCallSimulator.UI;

namespace ServiceDeskCallSimulator.Tests;

/// <summary>
/// A high-DPI/STA layout smoke test. It constructs the real <see cref="MainForm"/> and forces
/// child control handle creation (<see cref="Control.CreateControl()"/>) without ever calling
/// <c>Show()</c>, so <c>OnShown</c> never fires and no Azure, Dev Tunnel, Speech, model, PSTN,
/// or audio-device access occurs. Every assertion is purely structural.
/// </summary>
[Collection(StaUiCollection.Name)]
public sealed class MainFormLayoutTests
{
    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw failure;
        }
    }

    private static (SimulatorSettings Settings, ServiceProvider Services) CreateComposition()
    {
        var settings = new SimulatorSettings();
        var services = new ServiceCollection()
            .AddServiceDeskCallSimulatorCore(settings)
            .BuildServiceProvider();
        return (settings, services);
    }

    [Fact]
    public void MainForm_ConstructionAndLayout_NeverTouchesExternalResourcesOrShowsTheWindow()
    {
        RunOnSta(() =>
        {
            var (settings, services) = CreateComposition();
            using (services)
            using (var form = new MainForm(settings, services))
            {
                // Forces handle creation (and therefore layout) for the form and its children
                // without calling Show()/setting Visible=true, so Shown-driven initialization
                // never runs.
                form.CreateControl();
                _ = form.Handle;

                Assert.Equal(AppPhase.Initializing, form.CurrentViewState.Phase);
                Assert.True(form.IsHandleCreated);
                Assert.False(form.Visible);
            }
        });
    }

    [Fact]
    public void MainForm_HasHighDpiAwareAutoScaleModeAndASensibleMinimumSize()
    {
        RunOnSta(() =>
        {
            var (settings, services) = CreateComposition();
            using (services)
            using (var form = new MainForm(settings, services))
            {
                form.CreateControl();

                Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleMode);
                Assert.True(form.MinimumSize.Width >= 900, "The form should enforce a sensible minimum width.");
                Assert.True(form.MinimumSize.Height >= 600, "The form should enforce a sensible minimum height.");
                Assert.Equal(FormBorderStyle.Sizable, form.FormBorderStyle);
                Assert.True(form.MaximizeBox);
            }
        });
    }

    [Fact]
    public void MainForm_RootLayout_IsAThreeRowTableLayoutPanel()
    {
        RunOnSta(() =>
        {
            var (settings, services) = CreateComposition();
            using (services)
            using (var form = new MainForm(settings, services))
            {
                form.CreateControl();

                var root = Assert.Single(form.Controls.OfType<TableLayoutPanel>(), p => p.Name == "RootLayoutPanel");
                Assert.Equal(DockStyle.Fill, root.Dock);
                Assert.Equal(3, root.RowCount);
            }
        });
    }

    [Fact]
    public void MainForm_WorkingArea_UsesASplitContainerWithAFixedLeftSetupPane()
    {
        RunOnSta(() =>
        {
            var (settings, services) = CreateComposition();
            using (services)
            using (var form = new MainForm(settings, services))
            {
                form.CreateControl();

                Assert.Equal(FixedPanel.Panel1, form.WorkingAreaSplitContainer.FixedPanel);
                Assert.Equal(DockStyle.Fill, form.WorkingAreaSplitContainer.Dock);

                var routingGroupBox = FindDescendant<GroupBox>(form.WorkingAreaSplitContainer.Panel1, "RoutingGroupBox");
                var scriptGroupBox = FindDescendant<GroupBox>(form.WorkingAreaSplitContainer.Panel1, "ScriptGroupBox");
                Assert.NotNull(routingGroupBox);
                Assert.NotNull(scriptGroupBox);
                Assert.Equal("Routing", routingGroupBox!.Text);
                Assert.Equal("Caller script", scriptGroupBox!.Text);
            }
        });
    }

    [Fact]
    public void MainForm_RoutingGroupBox_ContainsRequiredControlsWithAccessibleNamesAndErrorProvider()
    {
        RunOnSta(() =>
        {
            var (settings, services) = CreateComposition();
            using (services)
            using (var form = new MainForm(settings, services))
            {
                form.CreateControl();

                Assert.Equal("Routing", form.RoutingGroupBox.AccessibleName);
                Assert.Equal(ComboBoxStyle.DropDownList, form.CallerIdComboBox.DropDownStyle);
                Assert.False(string.IsNullOrWhiteSpace(form.CallerIdComboBox.AccessibleName));
                Assert.False(string.IsNullOrWhiteSpace(form.RefreshNumbersButton.AccessibleName));
                Assert.False(string.IsNullOrWhiteSpace(form.DestinationTextBox.AccessibleName));
                Assert.NotNull(form.RoutingErrorProvider);
                Assert.IsType<ErrorProvider>(form.RoutingErrorProvider);
            }
        });
    }

    [Fact]
    public void MainForm_ScriptGroupBox_UsesAPresetDropDownListWithReadOnlyLocaleAndVoiceLabels()
    {
        RunOnSta(() =>
        {
            var (settings, services) = CreateComposition();
            using (services)
            using (var form = new MainForm(settings, services))
            {
                form.CreateControl();

                Assert.Equal(ComboBoxStyle.DropDownList, form.PresetComboBox.DropDownStyle);
                Assert.False(string.IsNullOrWhiteSpace(form.PresetComboBox.AccessibleName));

                // Locale/voice are plain read-only Labels, not editable controls, and there is
                // no separate language ComboBox/selector anywhere in the setup pane.
                Assert.IsType<Label>(form.LocaleValueLabel);
                Assert.IsType<Label>(form.VoiceValueLabel);
                var comboBoxesInSetupPane = form.WorkingAreaSplitContainer.Panel1.Controls
                    .OfType<Control>()
                    .SelectMany(FlattenDescendants)
                    .OfType<ComboBox>()
                    .ToArray();
                Assert.Equal(2, comboBoxesInSetupPane.Length); // caller ID + preset only
            }
        });
    }

    [Fact]
    public void MainForm_ScriptFields_UseMultilineTextBoxesForLongFreeTextFields()
    {
        RunOnSta(() =>
        {
            var (settings, services) = CreateComposition();
            using (services)
            using (var form = new MainForm(settings, services))
            {
                form.CreateControl();

                Assert.True(form.BackgroundTextBox.Multiline);
                Assert.True(form.ReasonTextBox.Multiline);
                Assert.True(form.AdditionalDetailsTextBox.Multiline);
                Assert.False(form.IdentityTextBox.Multiline);
                Assert.False(form.UrgencyTextBox.Multiline);
                Assert.False(form.CallbackNumberTextBox.Multiline);
                Assert.True(form.ScriptFieldsPanel.AutoScroll);
                Assert.False(string.IsNullOrWhiteSpace(form.ResetPresetButton.Text));
            }
        });
    }

    [Fact]
    public void MainForm_LiveCallPane_HasATranscriptAndDiagnosticsTab()
    {
        RunOnSta(() =>
        {
            var (settings, services) = CreateComposition();
            using (services)
            using (var form = new MainForm(settings, services))
            {
                form.CreateControl();

                Assert.Equal(2, form.ConversationTabControl.TabPages.Count);
                Assert.Equal("Transcript", form.ConversationTabControl.TabPages[0].Text);
                Assert.Equal("Diagnostics", form.ConversationTabControl.TabPages[1].Text);
                Assert.True(form.TranscriptRichTextBox.ReadOnly);
                Assert.True(form.DiagnosticsRichTextBox.ReadOnly);
                Assert.False(string.IsNullOrWhiteSpace(form.TranscriptRichTextBox.AccessibleName));
                Assert.False(string.IsNullOrWhiteSpace(form.DiagnosticsRichTextBox.AccessibleName));
                Assert.False(string.IsNullOrWhiteSpace(form.ClearTranscriptButton.Text));
            }
        });
    }

    [Fact]
    public void MainForm_CommandBar_IsBottomDockedRightAlignedWithPrimaryAndDestructiveButtons()
    {
        RunOnSta(() =>
        {
            var (settings, services) = CreateComposition();
            using (services)
            using (var form = new MainForm(settings, services))
            {
                form.CreateControl();

                Assert.Equal(DockStyle.Bottom, form.CommandBarPanel.Dock);
                Assert.Equal(FlowDirection.RightToLeft, form.CommandBarPanel.FlowDirection);

                Assert.Contains("&Call", form.CallButton.Text, StringComparison.Ordinal);
                Assert.Contains("&Hang Up", form.HangUpButton.Text, StringComparison.Ordinal);
                Assert.Equal("Call", form.CallButton.AccessibleName);
                Assert.Equal("Hang up", form.HangUpButton.AccessibleName);
                Assert.Equal("Mute local audio", form.MuteLocalAudioCheckBox.AccessibleName);
                Assert.False(string.IsNullOrWhiteSpace(form.CallDisabledReasonLabel.AccessibleName));

                Assert.Contains(form.CallButton, form.CommandBarPanel.Controls.OfType<Control>());
                Assert.Contains(form.HangUpButton, form.CommandBarPanel.Controls.OfType<Control>());
                Assert.Contains(form.MuteLocalAudioCheckBox, form.CommandBarPanel.Controls.OfType<Control>());

                // Before initialization completes the Call button must not be enabled, and Hang
                // Up must never be enabled without an active call.
                Assert.False(form.CallButton.Enabled);
                Assert.False(form.HangUpButton.Enabled);
            }
        });
    }

    [Fact]
    public void MainForm_StatusHeader_ExposesChecklistRowsAndCopyButtonWithoutSerialModalDialogs()
    {
        RunOnSta(() =>
        {
            var (settings, services) = CreateComposition();
            using (services)
            using (var form = new MainForm(settings, services))
            {
                form.CreateControl();

                Assert.False(string.IsNullOrWhiteSpace(form.StatusBannerLabel.Text));
                Assert.False(string.IsNullOrWhiteSpace(form.StatusIconLabel.AccessibleName));
                Assert.False(string.IsNullOrWhiteSpace(form.AzureAuthChecklistLabel.Text));
                Assert.False(string.IsNullOrWhiteSpace(form.NumberDiscoveryChecklistLabel.Text));
                Assert.False(string.IsNullOrWhiteSpace(form.CallbackHostChecklistLabel.Text));
                Assert.False(string.IsNullOrWhiteSpace(form.DevTunnelChecklistLabel.Text));
                Assert.True(form.CallbackHostTextBox.ReadOnly);
                Assert.True(form.SelectedModelTextBox.ReadOnly);
                Assert.NotNull(form.CopyCallbackHostButton);
                Assert.False(form.RetryButton.Visible); // hidden until an initialization error occurs
                Assert.False(form.InitializationErrorLabel.Visible);
            }
        });
    }

    private static TControl? FindDescendant<TControl>(Control root, string name)
        where TControl : Control
    {
        foreach (var descendant in FlattenDescendants(root))
        {
            if (descendant is TControl typed && typed.Name == name)
            {
                return typed;
            }
        }

        return null;
    }

    private static IEnumerable<Control> FlattenDescendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var grandchild in FlattenDescendants(child))
            {
                yield return grandchild;
            }
        }
    }
}
