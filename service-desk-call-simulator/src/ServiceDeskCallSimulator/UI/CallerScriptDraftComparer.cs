using ServiceDeskCallSimulator.Presets;

namespace ServiceDeskCallSimulator.UI;

/// <summary>
/// Compares two editable caller script drafts field-by-field so the UI can detect unsaved
/// edits before switching presets.
/// </summary>
public static class CallerScriptDraftComparer
{
    /// <summary>
    /// Returns whether every editable field is equal between the two drafts.
    /// </summary>
    public static bool AreEqual(CallerScriptDraft? left, CallerScriptDraft? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            && string.Equals(left.Locale, right.Locale, StringComparison.Ordinal)
            && string.Equals(left.Voice, right.Voice, StringComparison.Ordinal)
            && string.Equals(left.OpeningLine, right.OpeningLine, StringComparison.Ordinal)
            && string.Equals(left.Identity, right.Identity, StringComparison.Ordinal)
            && string.Equals(left.Background, right.Background, StringComparison.Ordinal)
            && string.Equals(left.Reason, right.Reason, StringComparison.Ordinal)
            && string.Equals(left.Urgency, right.Urgency, StringComparison.Ordinal)
            && string.Equals(left.CallbackNumber, right.CallbackNumber, StringComparison.Ordinal)
            && string.Equals(left.AdditionalDetails, right.AdditionalDetails, StringComparison.Ordinal);
    }
}
