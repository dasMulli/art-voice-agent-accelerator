using System.Drawing;

namespace ServiceDeskCallSimulator.UI;

/// <summary>
/// The pure, WinForms-independent chrome sizing policy of the main window: the logical (96 DPI)
/// metrics of the status header and command bar, how they scale with DPI, and how much of the
/// client height must remain for the working area.
/// </summary>
/// <remarks>
/// This type is the single source of truth for those numbers. <c>MainForm.Designer.cs</c> applies
/// them to controls and the tests assert against them, so the designer and the regression tests
/// can never drift apart. Keeping the arithmetic here also makes the DPI behaviour testable
/// without creating, showing, or measuring a native window.
/// </remarks>
public static class ChromeLayoutMetrics
{
    /// <summary>The design baseline DPI that every logical metric below is expressed in.</summary>
    public const int DesignDpi = 96;

    /// <summary>Height cap of the whole status header, in logical pixels.</summary>
    public const int HeaderMaxLogicalHeight = 176;

    /// <summary>Height cap of the whole bottom command bar, in logical pixels.</summary>
    public const int CommandBarMaxLogicalHeight = 60;

    /// <summary>Initial client width of the window, in logical pixels.</summary>
    public const int InitialLogicalClientWidth = 1160;

    /// <summary>Initial client height of the window, in logical pixels.</summary>
    public const int InitialLogicalClientHeight = 760;

    /// <summary>Minimum window width, in logical pixels.</summary>
    public const int MinimumLogicalWindowWidth = 960;

    /// <summary>Minimum window height, in logical pixels.</summary>
    public const int MinimumLogicalWindowHeight = 680;

    /// <summary>
    /// Minimum client height, in logical pixels: the minimum window height less the usual caption
    /// and border chrome. This is the smallest client area the sizing policy must still work in.
    /// </summary>
    public const int MinimumLogicalClientHeight = 640;

    /// <summary>
    /// The share of the client height the working <c>SplitContainer</c> must keep. The reported
    /// live-smoke defect left it with roughly 6%.
    /// </summary>
    public const double MinimumWorkingAreaShare = 0.60;

    /// <summary>Explicit size of the primary command-bar buttons (Call / Hang Up).</summary>
    public static readonly Size CommandButtonLogicalSize = new(104, 30);

    /// <summary>Hard upper bound of the primary command-bar buttons.</summary>
    public static readonly Size CommandButtonMaxLogicalSize = new(160, 40);

    /// <summary>Explicit size of the secondary inline buttons (Copy / Retry).</summary>
    public static readonly Size SecondaryButtonLogicalSize = new(80, 26);

    /// <summary>Hard upper bound of the secondary inline buttons.</summary>
    public static readonly Size SecondaryButtonMaxLogicalSize = new(120, 34);

    /// <summary>Maximum width of the inline initialization error text, in logical pixels.</summary>
    public const int InitializationErrorMaxLogicalWidth = 720;

    /// <summary>Maximum width of the call-disabled reason text, in logical pixels.</summary>
    public const int CallDisabledReasonMaxLogicalWidth = 420;

    /// <summary>
    /// A finite logical length that stands in for "no bound" in <see cref="Control.MaximumSize"/>.
    /// </summary>
    /// <remarks>
    /// WinForms documents <c>0</c> as the unbounded sentinel in <c>MaximumSize</c>, but the
    /// sentinel does not survive DPI scaling: scaling a bordered auto-sized container whose
    /// <c>MaximumSize</c> is <c>(0, h)</c> turns it into a real 2 px maximum width, which then
    /// collapses the control to its border. Using a finite value avoids the sentinel entirely and
    /// scales predictably (no overflow), while staying far larger than any real desktop - it grows
    /// with DPI exactly as the desktop's device pixels do.
    /// </remarks>
    public const int UnboundedLogicalLength = 8192;

    /// <summary>
    /// A <see cref="Control.MaximumSize"/> that bounds only the height. Never contains the
    /// zero sentinel.
    /// </summary>
    public static Size MaxHeight(int logicalHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(logicalHeight, 0);
        return new Size(UnboundedLogicalLength, logicalHeight);
    }

    /// <summary>
    /// A <see cref="Control.MaximumSize"/> that bounds only the width. Never contains the
    /// zero sentinel.
    /// </summary>
    public static Size MaxWidth(int logicalWidth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(logicalWidth, 0);
        return new Size(logicalWidth, UnboundedLogicalLength);
    }

    /// <summary>
    /// Returns the scale factor WinForms applies under <see cref="System.Windows.Forms.AutoScaleMode.Dpi"/>
    /// for the given monitor DPI, given the <see cref="DesignDpi"/> baseline.
    /// </summary>
    /// <remarks>
    /// The reported live-smoke defect was exactly this calculation done with the wrong unit: the
    /// form declared <c>AutoScaleDimensions = (7, 15)</c> (a font size) while using
    /// <c>AutoScaleMode.Dpi</c>, which yields 96/7 x 96/15 instead of 96/96.
    /// </remarks>
    public static double ScaleFactorFor(int dpi)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dpi, 0);
        return dpi / (double)DesignDpi;
    }

    /// <summary>Scales one logical length to device pixels for the given DPI.</summary>
    public static int Scale(int logicalLength, int dpi) =>
        (int)Math.Round(logicalLength * ScaleFactorFor(dpi), MidpointRounding.AwayFromZero);

    /// <summary>Scales one logical size to device pixels for the given DPI.</summary>
    public static Size Scale(Size logicalSize, int dpi) =>
        new(Scale(logicalSize.Width, dpi), Scale(logicalSize.Height, dpi));

    /// <summary>The header height cap in device pixels for the given DPI.</summary>
    public static int MaxHeaderHeight(int dpi) => Scale(HeaderMaxLogicalHeight, dpi);

    /// <summary>The command bar height cap in device pixels for the given DPI.</summary>
    public static int MaxCommandBarHeight(int dpi) => Scale(CommandBarMaxLogicalHeight, dpi);

    /// <summary>
    /// The minimum acceptable working-area height for a client area of
    /// <paramref name="clientHeight"/> device pixels.
    /// </summary>
    public static int MinimumWorkingAreaHeight(int clientHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(clientHeight);
        return (int)Math.Ceiling(clientHeight * MinimumWorkingAreaShare);
    }

    /// <summary>
    /// Returns true when a client area of <paramref name="clientHeight"/> device pixels still
    /// leaves the working area its required share once both chrome rows are at their caps.
    /// </summary>
    public static bool ChromeCapsLeaveEnoughWorkingArea(int clientHeight, int dpi) =>
        clientHeight - MaxHeaderHeight(dpi) - MaxCommandBarHeight(dpi)
            >= MinimumWorkingAreaHeight(clientHeight);

    /// <summary>
    /// Returns true when the window's initial size fits inside a desktop of the given device
    /// pixel size at the given DPI, so the operator is not forced to maximize it.
    /// </summary>
    public static bool InitialSizeFitsDesktop(int desktopWidth, int desktopHeight, int dpi) =>
        Scale(InitialLogicalClientWidth, dpi) <= desktopWidth
        && Scale(InitialLogicalClientHeight, dpi) <= desktopHeight;

    /// <summary>
    /// Returns true when the window's minimum size fits inside a desktop of the given device pixel
    /// size at the given DPI, so the window can never become unusable.
    /// </summary>
    public static bool MinimumSizeFitsDesktop(int desktopWidth, int desktopHeight, int dpi) =>
        Scale(MinimumLogicalWindowWidth, dpi) <= desktopWidth
        && Scale(MinimumLogicalWindowHeight, dpi) <= desktopHeight;
}
