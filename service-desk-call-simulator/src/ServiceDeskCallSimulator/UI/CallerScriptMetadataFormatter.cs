using ServiceDeskCallSimulator.Presets;

namespace ServiceDeskCallSimulator.UI;

/// <summary>
/// Formats the read-only locale and voice metadata shown next to the caller script. Presets with a
/// deterministic language switch render the initial-to-target transition and its threshold; every
/// other preset renders its single locale and voice unchanged.
/// </summary>
public static class CallerScriptMetadataFormatter
{
    /// <summary>
    /// Returns the locale label for the given draft.
    /// </summary>
    public static string FormatLocale(CallerScriptDraft? draft) =>
        Format(draft?.Locale, draft?.LanguageSwitch, policy => policy.TargetLocale, draft);

    /// <summary>
    /// Returns the voice label for the given draft.
    /// </summary>
    public static string FormatVoice(CallerScriptDraft? draft) =>
        Format(draft?.Voice, draft?.LanguageSwitch, policy => policy.TargetVoice, draft);

    private static string Format(
        string? initialValue,
        CallerLanguageSwitchPolicy? policy,
        Func<CallerLanguageSwitchPolicy, string> selectTarget,
        CallerScriptDraft? draft)
    {
        if (draft is null)
        {
            return string.Empty;
        }

        var initial = initialValue ?? string.Empty;
        if (policy is null)
        {
            return initial;
        }

        var turnWord = policy.AfterFinalServiceDeskTurns == 1 ? "turn" : "turns";
        return $"{initial} → {selectTarget(policy)} "
            + $"(after {policy.AfterFinalServiceDeskTurns} service desk {turnWord})";
    }
}
