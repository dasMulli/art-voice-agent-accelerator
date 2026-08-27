using ServiceDeskCallSimulator.UI;

namespace ServiceDeskCallSimulator.Tests;

/// <summary>
/// Pure, WinForms-free tests for the window chrome sizing policy and its DPI arithmetic. These
/// pin the numbers the designer applies, including the exact calculation that was wrong in the
/// reported live-smoke defect (a font dimension used as an <c>AutoScaleMode.Dpi</c> baseline).
/// </summary>
public sealed class ChromeLayoutMetricsTests
{
    [Theory]
    [InlineData(96, 1.0)]
    [InlineData(120, 1.25)]
    [InlineData(144, 1.5)]
    [InlineData(192, 2.0)]
    [InlineData(240, 2.5)]
    public void ScaleFactorFor_IsMonitorDpiOverTheNinetySixDesignBaseline(int dpi, double expected)
    {
        Assert.Equal(expected, ChromeLayoutMetrics.ScaleFactorFor(dpi), 6);
    }

    [Fact]
    public void ScaleFactorFor_UsesADpiBaselineNotAFontBaseline()
    {
        // The live-smoke defect: AutoScaleDimensions = (7, 15) with AutoScaleMode.Dpi produced a
        // 96/7 x 96/15 scale factor (~13.7x / ~6.4x) and inflated every explicitly sized control.
        Assert.Equal(96, ChromeLayoutMetrics.DesignDpi);
        Assert.Equal(1.0, ChromeLayoutMetrics.ScaleFactorFor(ChromeLayoutMetrics.DesignDpi), 6);
        Assert.NotEqual(96d / 7d, ChromeLayoutMetrics.ScaleFactorFor(96), 6);
        Assert.NotEqual(96d / 15d, ChromeLayoutMetrics.ScaleFactorFor(96), 6);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    [InlineData(240)]
    public void ChromeCaps_ScaleLinearlyAndStayCompact(int dpi)
    {
        var scale = ChromeLayoutMetrics.ScaleFactorFor(dpi);

        Assert.Equal(
            (int)Math.Round(ChromeLayoutMetrics.HeaderMaxLogicalHeight * scale, MidpointRounding.AwayFromZero),
            ChromeLayoutMetrics.MaxHeaderHeight(dpi));
        Assert.Equal(
            (int)Math.Round(ChromeLayoutMetrics.CommandBarMaxLogicalHeight * scale, MidpointRounding.AwayFromZero),
            ChromeLayoutMetrics.MaxCommandBarHeight(dpi));

        // Both chrome rows together must stay a minority of the initial client height.
        var clientHeight = ChromeLayoutMetrics.Scale(ChromeLayoutMetrics.InitialLogicalClientHeight, dpi);
        var chrome = ChromeLayoutMetrics.MaxHeaderHeight(dpi) + ChromeLayoutMetrics.MaxCommandBarHeight(dpi);
        Assert.True(chrome < clientHeight * 0.4, $"chrome={chrome}, client={clientHeight}, dpi={dpi}");
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    [InlineData(240)]
    public void ChromeCaps_AlwaysLeaveTheWorkingAreaItsRequiredShare(int dpi)
    {
        foreach (var logicalClientHeight in new[]
        {
            ChromeLayoutMetrics.MinimumLogicalClientHeight,
            ChromeLayoutMetrics.InitialLogicalClientHeight,
            1000,
        })
        {
            var clientHeight = ChromeLayoutMetrics.Scale(logicalClientHeight, dpi);
            Assert.True(
                ChromeLayoutMetrics.ChromeCapsLeaveEnoughWorkingArea(clientHeight, dpi),
                $"dpi={dpi}, clientHeight={clientHeight}, "
                + $"header={ChromeLayoutMetrics.MaxHeaderHeight(dpi)}, "
                + $"commandBar={ChromeLayoutMetrics.MaxCommandBarHeight(dpi)}");
        }
    }

    [Fact]
    public void MinimumWorkingAreaShare_IsSixtyPercentAndComputedFromTheClientHeight()
    {
        Assert.Equal(0.60, ChromeLayoutMetrics.MinimumWorkingAreaShare, 6);
        Assert.Equal(456, ChromeLayoutMetrics.MinimumWorkingAreaHeight(760));
        Assert.Equal(1312, ChromeLayoutMetrics.MinimumWorkingAreaHeight(2186));
    }

    /// <summary>
    /// The reported desktop was 5146x2186 device pixels. The window must fit it at every plausible
    /// scaling, so the operator is never forced into an unusable maximized window.
    /// </summary>
    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    [InlineData(240)]
    public void InitialAndMinimumSizes_FitTheReportedHighDpiFiveKDesktop(int dpi)
    {
        Assert.True(
            ChromeLayoutMetrics.InitialSizeFitsDesktop(5146, 2186, dpi),
            $"initial {ChromeLayoutMetrics.Scale(ChromeLayoutMetrics.InitialLogicalClientWidth, dpi)}"
            + $"x{ChromeLayoutMetrics.Scale(ChromeLayoutMetrics.InitialLogicalClientHeight, dpi)} at dpi {dpi}");
        Assert.True(
            ChromeLayoutMetrics.MinimumSizeFitsDesktop(5146, 2186, dpi),
            $"minimum {ChromeLayoutMetrics.Scale(ChromeLayoutMetrics.MinimumLogicalWindowWidth, dpi)}"
            + $"x{ChromeLayoutMetrics.Scale(ChromeLayoutMetrics.MinimumLogicalWindowHeight, dpi)} at dpi {dpi}");
    }

    [Fact]
    public void MinimumWindowSize_StaysAtOrAboveThePlansPracticalMinimumAtNormalDpi()
    {
        Assert.True(ChromeLayoutMetrics.MinimumLogicalWindowWidth >= 900);
        Assert.True(ChromeLayoutMetrics.MinimumLogicalWindowHeight >= 600);
        Assert.True(ChromeLayoutMetrics.InitialLogicalClientWidth > ChromeLayoutMetrics.MinimumLogicalWindowWidth);
        Assert.True(ChromeLayoutMetrics.InitialLogicalClientHeight > ChromeLayoutMetrics.MinimumLogicalWindowHeight);
        Assert.True(
            ChromeLayoutMetrics.MinimumLogicalClientHeight < ChromeLayoutMetrics.MinimumLogicalWindowHeight,
            "The minimum client height must exclude the caption and border chrome.");
    }

    [Fact]
    public void CommandButtonSizes_AreExplicitAndBounded()
    {
        Assert.True(ChromeLayoutMetrics.CommandButtonLogicalSize.Width > 0);
        Assert.True(ChromeLayoutMetrics.CommandButtonLogicalSize.Height > 0);
        Assert.True(
            ChromeLayoutMetrics.CommandButtonMaxLogicalSize.Width >= ChromeLayoutMetrics.CommandButtonLogicalSize.Width);
        Assert.True(
            ChromeLayoutMetrics.CommandButtonMaxLogicalSize.Height >= ChromeLayoutMetrics.CommandButtonLogicalSize.Height);

        // The primary buttons must fit inside the command bar cap at every DPI.
        foreach (var dpi in new[] { 96, 144, 192, 240 })
        {
            Assert.True(
                ChromeLayoutMetrics.Scale(ChromeLayoutMetrics.CommandButtonMaxLogicalSize, dpi).Height
                    <= ChromeLayoutMetrics.MaxCommandBarHeight(dpi),
                $"dpi={dpi}");
        }

        Assert.True(
            ChromeLayoutMetrics.SecondaryButtonMaxLogicalSize.Width
                <= ChromeLayoutMetrics.CommandButtonMaxLogicalSize.Width);
    }

    [Fact]
    public void UnboundedLogicalLength_IsFiniteAvoidsTheZeroSentinelAndScalesWithoutOverflow()
    {
        Assert.True(ChromeLayoutMetrics.UnboundedLogicalLength > 0);

        foreach (var dpi in new[] { 96, 144, 192, 240, 288 })
        {
            var scaled = ChromeLayoutMetrics.Scale(ChromeLayoutMetrics.UnboundedLogicalLength, dpi);
            Assert.True(scaled > 0, $"overflowed at dpi {dpi}");
            Assert.True(
                scaled > 5146,
                $"The 'unbounded' stand-in must stay larger than the reported 5146 px desktop "
                + $"at dpi {dpi} (was {scaled}).");
        }

        // Neither helper may ever emit the zero sentinel, in either component.
        var maxHeight = ChromeLayoutMetrics.MaxHeight(ChromeLayoutMetrics.HeaderMaxLogicalHeight);
        var maxWidth = ChromeLayoutMetrics.MaxWidth(ChromeLayoutMetrics.InitializationErrorMaxLogicalWidth);
        Assert.True(maxHeight.Width > 0 && maxHeight.Height > 0);
        Assert.True(maxWidth.Width > 0 && maxWidth.Height > 0);
        Assert.Equal(ChromeLayoutMetrics.HeaderMaxLogicalHeight, maxHeight.Height);
        Assert.Equal(ChromeLayoutMetrics.InitializationErrorMaxLogicalWidth, maxWidth.Width);

        Assert.Throws<ArgumentOutOfRangeException>(() => ChromeLayoutMetrics.MaxHeight(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChromeLayoutMetrics.MaxWidth(0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-96)]
    public void ScaleFactorFor_RejectsNonPositiveDpi(int dpi)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChromeLayoutMetrics.ScaleFactorFor(dpi));
    }
}
