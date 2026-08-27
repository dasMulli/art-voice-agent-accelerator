using System.Runtime.ExceptionServices;

namespace ServiceDeskCallSimulator.Tests;

/// <summary>
/// Serializes every WinForms test class so no two of them build controls concurrently.
/// </summary>
/// <remarks>
/// There is deliberately no collection fixture and no shared UI thread here. UI tests must never
/// show a native window: a shown <see cref="RichTextBox"/> that later receives <c>WM_SETFONT</c>
/// throws <see cref="System.Runtime.InteropServices.SEHException"/> from the RichEdit window
/// procedure, which WinForms surfaces as an interactive just-in-time debugging dialog inside
/// <c>testhost</c>. Layout is therefore validated without ever creating a window handle.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StaUiCollection
{
    public const string Name = "STA UI";
}

/// <summary>
/// Runs a WinForms action on a dedicated STA thread that has no message pump and shows nothing.
/// </summary>
internal static class StaRunner
{
    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            // Preserve the original stack trace, so an assertion failure points at the assertion
            // rather than at this helper.
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
