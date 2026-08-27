namespace ServiceDeskCallSimulator.Presets;

/// <summary>
/// Immutable caller facts captured when a call starts.
/// </summary>
public sealed record CallerScriptSnapshot(
    string Name,
    string Locale,
    string Voice,
    string OpeningLine,
    string Identity,
    string Background,
    string Reason,
    string Urgency,
    string CallbackNumber,
    string AdditionalDetails,
    CallerLanguageSwitchPolicy? LanguageSwitch = null)
{
    /// <summary>
    /// Copies an editable draft into the immutable facts used for one call.
    /// </summary>
    public static CallerScriptSnapshot FromDraft(CallerScriptDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return new CallerScriptSnapshot(
            RequireValue(draft.Name, nameof(draft.Name)),
            RequireValue(draft.Locale, nameof(draft.Locale)),
            RequireValue(draft.Voice, nameof(draft.Voice)),
            RequireValue(draft.OpeningLine, nameof(draft.OpeningLine)),
            RequireValue(draft.Identity, nameof(draft.Identity)),
            RequireValue(draft.Background, nameof(draft.Background)),
            RequireValue(draft.Reason, nameof(draft.Reason)),
            RequireValue(draft.Urgency, nameof(draft.Urgency)),
            RequireValue(draft.CallbackNumber, nameof(draft.CallbackNumber)),
            RequireValue(draft.AdditionalDetails, nameof(draft.AdditionalDetails)),
            draft.LanguageSwitch?.Validated(nameof(draft.LanguageSwitch)));
    }

    private static string RequireValue(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Caller script fields must not be blank.", fieldName);
        }

        return value.Trim();
    }
}
