using Microsoft.Extensions.DependencyInjection;
using ServiceDeskCallSimulator.Azure;
using ServiceDeskCallSimulator.Configuration;
using ServiceDeskCallSimulator.UI;

namespace ServiceDeskCallSimulator.Tests;

/// <summary>
/// Measures the real <see cref="MainForm"/> layout at representative client sizes and DPI scale
/// factors <b>without ever showing a window or creating a native handle</b>, and asserts the
/// result against the shared <see cref="ChromeLayoutMetrics"/> policy.
/// </summary>
/// <remarks>
/// <para>
/// These tests exist because purely structural assertions (control types, docking, accessible
/// names) missed the reported live-smoke defect, where the status header rendered ~1255 px high
/// and the command bar ~548 px high and left the working area ~153 px on a 5146x2186 desktop.
/// </para>
/// <para>
/// They deliberately do not call <c>Show()</c>. A shown <see cref="RichTextBox"/> that later
/// receives <c>WM_SETFONT</c> throws <see cref="System.Runtime.InteropServices.SEHException"/>
/// out of the RichEdit window procedure, which WinForms surfaces as an interactive just-in-time
/// debugging dialog inside <c>testhost</c> and blocks the run. WinForms layout does not need
/// window handles: <see cref="Control.PerformLayout()"/> assigns real bounds from the same layout
/// engines the shown window uses, so the measurements below are the ones the operator sees, with
/// no native window involved. Every test additionally asserts that no handle was created.
/// </para>
/// </remarks>
[Collection(StaUiCollection.Name)]
public sealed class MainFormChromeLayoutTests
{
    /// <summary>Chrome and header rows must span at least this share of their container width.</summary>
    private const double MinimumSpanShare = 0.95;

    public static TheoryData<int, int, int> LayoutCases() => new()
    {
        // monitor DPI, logical client width, logical client height
        { 96, ChromeLayoutMetrics.InitialLogicalClientWidth, ChromeLayoutMetrics.InitialLogicalClientHeight },
        { 96, ChromeLayoutMetrics.MinimumLogicalWindowWidth, ChromeLayoutMetrics.MinimumLogicalWindowHeight },
        { 96, 2400, 900 },
        { 144, ChromeLayoutMetrics.InitialLogicalClientWidth, ChromeLayoutMetrics.InitialLogicalClientHeight },
        { 192, ChromeLayoutMetrics.InitialLogicalClientWidth, ChromeLayoutMetrics.InitialLogicalClientHeight },
        { 192, 1600, 1000 },
    };

    [Theory]
    [MemberData(nameof(LayoutCases))]
    public void Layout_KeepsChromeBoundedAndGivesTheWorkingAreaMostOfTheClientHeight(
        int dpi,
        int logicalClientWidth,
        int logicalClientHeight)
    {
        WithLaidOutForm(
            dpi,
            ChromeLayoutMetrics.Scale(logicalClientWidth, dpi),
            ChromeLayoutMetrics.Scale(logicalClientHeight, dpi),
            form => AssertBoundedChrome(form, dpi));
    }

    /// <summary>
    /// Reproduces the reported live-smoke desktop: the window filling 5146x2186 device pixels at
    /// 100%, 150%, and 200% scaling.
    /// </summary>
    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void Layout_OnTheReportedHighDpiFiveKDesktop_DoesNotStarveTheWorkingArea(int dpi)
    {
        WithLaidOutForm(dpi, 5146, 2186, form => AssertBoundedChrome(form, dpi));
    }

    [Fact]
    public void Layout_StaysBoundedWhileTheInlineInitializationErrorAndRetryAreVisible()
    {
        WithLaidOutForm(96, 1160, 760, form =>
        {
            var collapsedErrorRowHeight = form.InitializationErrorRow.Height;

            form.InitializationErrorLabel.Text =
                "Azure authentication did not complete within 25 seconds. "
                + "Sign in with 'az login' (or Visual Studio) and select Retry.";
            form.InitializationErrorLabel.Visible = true;
            form.RetryButton.Visible = true;
            form.PerformLayout();

            // Control.Visible reports effective visibility, which is false while the form itself is
            // not shown; layout however uses the desired flag, so the error row really is measured.
            Assert.True(
                form.InitializationErrorRow.Height > collapsedErrorRowHeight,
                "Showing the inline error and Retry must actually grow the error row.");
            AssertBoundedChrome(form, dpi: 96);
        });
    }

    [Fact]
    public void StatusHeader_IsARowBasedTableLayoutPanelWithOneRowPerHeaderConcern()
    {
        WithLaidOutForm(96, 1160, 760, form =>
        {
            var header = form.StatusHeaderPanel;
            Assert.Equal(1, header.ColumnCount);
            Assert.Equal(5, header.RowCount);
            Assert.Equal(5, header.RowStyles.Count);
            Assert.All(
                header.RowStyles.Cast<RowStyle>(),
                style => Assert.Equal(SizeType.AutoSize, style.SizeType));

            // Rows are real cells, in order, not overlapping docked children.
            Assert.Equal(new TableLayoutPanelCellPosition(0, 0), header.GetCellPosition(form.StatusRow));
            Assert.Equal(new TableLayoutPanelCellPosition(0, 1), header.GetCellPosition(form.ChecklistPanel));
            Assert.Equal(new TableLayoutPanelCellPosition(0, 2), header.GetCellPosition(form.CallbackHostRow));
            Assert.Equal(new TableLayoutPanelCellPosition(0, 3), header.GetCellPosition(form.SelectedModelRow));
            Assert.Equal(new TableLayoutPanelCellPosition(0, 4), header.GetCellPosition(form.InitializationErrorRow));

            // Laid-out rows must not overlap each other.
            var rows = new Control[]
            {
                form.StatusRow,
                form.ChecklistPanel,
                form.CallbackHostRow,
                form.SelectedModelRow,
            };
            for (var i = 1; i < rows.Length; i++)
            {
                Assert.True(
                    rows[i].Top >= rows[i - 1].Bottom,
                    $"'{rows[i].Name}' (top {rows[i].Top}) overlaps '{rows[i - 1].Name}' (bottom {rows[i - 1].Bottom}).");
            }
        });
    }

    [Fact]
    public void CommandBarButtons_HaveExplicitBoundedLogicalSizesAndDoNotStretch()
    {
        WithLaidOutForm(96, 2400, 900, form =>
        {
            foreach (var button in new[] { form.CallButton, form.HangUpButton })
            {
                Assert.False(button.AutoSize);
                Assert.Equal(AnchorStyles.None, button.Anchor);
                Assert.Equal(ChromeLayoutMetrics.CommandButtonMaxLogicalSize, button.MaximumSize);
                Assert.Equal(ChromeLayoutMetrics.CommandButtonLogicalSize, button.Size);
            }

            // A much wider window must not widen or heighten the buttons at all.
            var sizeAtDefault = form.CallButton.Size;
            form.ClientSize = new Size(4000, 900);
            form.PerformLayout();

            Assert.Equal(sizeAtDefault, form.CallButton.Size);
            AssertBoundedChrome(form, dpi: 96);
        });
    }

    [Fact]
    public void LayoutHarness_NeverCreatesAWindowHandleShowsAWindowOrStartsInitialization()
    {
        WithLaidOutForm(192, 2320, 1520, form =>
        {
            Assert.False(form.Visible, "The layout harness must never show a window.");
            Assert.False(form.IsHandleCreated, "The layout harness must never create a native handle.");
            Assert.All(
                Descendants(form),
                child => Assert.False(
                    child.IsHandleCreated,
                    $"'{child.Name}' created a native handle in the layout harness."));

            Assert.Equal(AppPhase.Initializing, form.CurrentViewState.Phase);
            Assert.Equal(
                InitializationStageStatus.Pending,
                form.CurrentViewState.Checklist
                    .Single(item => item.Stage == InitializationStage.AzureAuthentication)
                    .Status);
        });
    }

    /// <summary>
    /// Guards against the exact regression that caused the defect: an <c>AutoScaleMode.Dpi</c>
    /// form whose <c>AutoScaleDimensions</c> is a font size rather than the 96 DPI baseline.
    /// </summary>
    [Fact]
    public void Form_DeclaresItsAutoScaleBaselineInDpiUnits()
    {
        WithLaidOutForm(96, 1160, 760, form =>
        {
            Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleMode);
            Assert.Equal(ChromeLayoutMetrics.DesignDpi, form.AutoScaleDimensions.Width);
            Assert.Equal(ChromeLayoutMetrics.DesignDpi, form.AutoScaleDimensions.Height);
        });
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static void WithLaidOutForm(int dpi, int clientWidth, int clientHeight, Action<MainForm> assert)
    {
        StaRunner.Run(() =>
        {
            var settings = new SimulatorSettings();
            using var services = new ServiceCollection()
                .AddServiceDeskCallSimulatorCore(settings)
                .BuildServiceProvider();
            using var form = MainForm.CreateForLayoutMeasurement(settings, services);

            LayOut(form, dpi, clientWidth, clientHeight);

            // Never negotiable, in every case: the harness must stay handle-free and invisible.
            Assert.False(form.Visible);
            Assert.False(form.IsHandleCreated);

            assert(form);
        });
    }

    /// <summary>
    /// Applies the DPI scale factor WinForms applies under <see cref="AutoScaleMode.Dpi"/>, then
    /// lays the form out at the requested client size.
    /// </summary>
    /// <remarks>
    /// Changing fonts here is safe precisely because no handle exists: <c>WM_SETFONT</c> is only
    /// sent to controls whose handle has been created, so the RichEdit SEH failure that produced
    /// interactive JIT dialogs cannot occur.
    /// </remarks>
    private static void LayOut(MainForm form, int dpi, int clientWidth, int clientHeight)
    {
        form.MinimumSize = Size.Empty;

        if (dpi != ChromeLayoutMetrics.DesignDpi)
        {
            var scale = (float)ChromeLayoutMetrics.ScaleFactorFor(dpi);

            // The ambient form font drives every AutoSize measurement; the two header labels carry
            // an explicit bold font and therefore do not inherit it.
            form.Font = new Font(
                form.Font.FontFamily,
                form.Font.Size * scale,
                form.Font.Style,
                form.Font.Unit);
            form.StatusIconLabel.Font = new Font(form.Font, FontStyle.Bold);
            form.StatusBannerLabel.Font = new Font(form.Font, FontStyle.Bold);

            // Scale() applies the same factor WinForms applies to Size, MinimumSize, MaximumSize,
            // Padding and Margin on a DPI change.
            form.Scale(new SizeF(scale, scale));
            form.MinimumSize = Size.Empty;
        }

        form.ClientSize = new Size(clientWidth, clientHeight);
        form.PerformLayout();
    }

    private static void AssertBoundedChrome(MainForm form, int dpi)
    {
        var clientHeight = form.ClientSize.Height;
        var clientWidth = form.ClientSize.Width;
        var root = form.RootLayoutPanel;
        var header = form.StatusHeaderPanel;
        var commandBar = form.CommandBarPanel;
        var workingArea = form.WorkingAreaSplitContainer;

        var context =
            $"dpi={dpi}, client={form.ClientSize}, root={root.Size}, header={header.Size}, "
            + $"commandBar={commandBar.Size}, workingArea={workingArea.Size}, "
            + $"call={form.CallButton.Size}, hangUp={form.HangUpButton.Size}";

        // ---- Width: the header must span the window, not collapse to its border ------------
        // Regression guard for the live defect where StatusHeaderPanel rendered 2 px wide inside
        // a 1740 px root because MaximumSize's zero "unbounded" sentinel did not survive DPI
        // scaling and became a real 2 px maximum width.
        Assert.True(
            root.Width >= clientWidth * MinimumSpanShare,
            $"The root layout does not span the client width. {context}");
        Assert.True(
            header.Width >= root.Width * MinimumSpanShare,
            $"The status header does not span the root width "
            + $"(needs >= {(int)(root.Width * MinimumSpanShare)}). {context}");
        Assert.True(
            header.Width >= clientWidth * MinimumSpanShare,
            $"The status header does not span the client width. {context}");
        Assert.True(
            commandBar.Width >= root.Width * MinimumSpanShare,
            $"The command bar does not span the root width. {context}");
        Assert.True(
            workingArea.Width >= root.Width * MinimumSpanShare,
            $"The working area does not span the root width. {context}");

        foreach (var row in HeaderRows(form))
        {
            Assert.True(
                row.Width >= header.ClientSize.Width * MinimumSpanShare,
                $"Header row '{row.Name}' is {row.Width} px wide and does not span the header "
                + $"client width {header.ClientSize.Width}. {context}");
        }

        // The read-only fields must get real width from their percent columns.
        Assert.True(
            form.CallbackHostTextBox.Width >= header.ClientSize.Width * 0.4,
            $"The callback host field collapsed to {form.CallbackHostTextBox.Width} px. {context}");
        Assert.True(
            form.SelectedModelTextBox.Width >= header.ClientSize.Width * 0.4,
            $"The selected model field collapsed to {form.SelectedModelTextBox.Width} px. {context}");

        // ---- Height --------------------------------------------------------------------------
        Assert.True(
            header.Height <= ChromeLayoutMetrics.MaxHeaderHeight(dpi),
            $"The status header exceeded its scaled height cap "
            + $"({ChromeLayoutMetrics.MaxHeaderHeight(dpi)}). {context}");
        Assert.True(
            commandBar.Height <= ChromeLayoutMetrics.MaxCommandBarHeight(dpi),
            $"The command bar exceeded its scaled height cap "
            + $"({ChromeLayoutMetrics.MaxCommandBarHeight(dpi)}). {context}");

        // Guard against a vacuously passing measurement: the chrome must actually be laid out.
        Assert.True(
            header.Height >= ChromeLayoutMetrics.Scale(60, dpi),
            $"The status header was not laid out; it is implausibly short. {context}");
        Assert.True(
            commandBar.Height >= ChromeLayoutMetrics.Scale(24, dpi),
            $"The command bar was not laid out; it is implausibly short. {context}");
        Assert.InRange(
            header.Height + workingArea.Height + commandBar.Height,
            clientHeight - 8,
            clientHeight);

        Assert.True(
            workingArea.Height >= ChromeLayoutMetrics.MinimumWorkingAreaHeight(clientHeight),
            $"The working area must keep at least "
            + $"{ChromeLayoutMetrics.MinimumWorkingAreaShare:P0} of the client height "
            + $"({ChromeLayoutMetrics.MinimumWorkingAreaHeight(clientHeight)}). {context}");

        // The header cap must bound the header without ever clipping one of its rows.
        foreach (var row in HeaderRows(form))
        {
            Assert.True(
                row.Bottom <= header.ClientSize.Height,
                $"Header row '{row.Name}' (bottom {row.Bottom}) is clipped by the header height cap "
                + $"(client height {header.ClientSize.Height}). {context}");
        }

        AssertNoZeroSentinelMaximumSize(form);

        AssertButtonBounded(form.CallButton, ChromeLayoutMetrics.CommandButtonMaxLogicalSize, dpi, context);
        AssertButtonBounded(form.HangUpButton, ChromeLayoutMetrics.CommandButtonMaxLogicalSize, dpi, context);
        AssertButtonBounded(form.CopyCallbackHostButton, ChromeLayoutMetrics.SecondaryButtonMaxLogicalSize, dpi, context);
        AssertButtonBounded(form.RetryButton, ChromeLayoutMetrics.SecondaryButtonMaxLogicalSize, dpi, context);
    }

    /// <summary>
    /// <c>Control.MaximumSize</c> documents 0 as "unbounded", but that sentinel is not preserved by
    /// DPI scaling, so a half-zero <c>MaximumSize</c> can silently turn into a real 2 px bound.
    /// No control in this form may rely on it.
    /// </summary>
    private static void AssertNoZeroSentinelMaximumSize(MainForm form)
    {
        foreach (var control in Descendants(form).Prepend(form))
        {
            var max = control.MaximumSize;
            if (max == Size.Empty)
            {
                continue;
            }

            Assert.True(
                max.Width > 0 && max.Height > 0,
                $"'{control.Name}' ({control.GetType().Name}) uses the zero 'unbounded' sentinel in "
                + $"MaximumSize {max}; that sentinel does not survive DPI scaling. Use "
                + $"ChromeLayoutMetrics.MaxWidth/MaxHeight instead.");
        }
    }

    private static Control[] HeaderRows(MainForm form) =>
    [
        form.StatusRow,
        form.ChecklistPanel,
        form.CallbackHostRow,
        form.SelectedModelRow,
        form.InitializationErrorRow,
    ];

    private static void AssertButtonBounded(Button button, Size maxLogicalSize, int dpi, string context)
    {
        var max = ChromeLayoutMetrics.Scale(maxLogicalSize, dpi);
        Assert.True(
            button.Width <= max.Width,
            $"'{button.Name}' stretched horizontally to {button.Width} (max {max.Width}). {context}");
        Assert.True(
            button.Height <= max.Height,
            $"'{button.Name}' stretched vertically to {button.Height} (max {max.Height}). {context}");
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var grandchild in Descendants(child))
            {
                yield return grandchild;
            }
        }
    }
}
