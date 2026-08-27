using ServiceDeskCallSimulator.Presets;

namespace ServiceDeskCallSimulator.Conversation;

/// <summary>
/// The response language in force for one caller turn.
/// </summary>
/// <param name="Locale">The caller response locale, for example <c>pl-PL</c>.</param>
/// <param name="LanguageName">The human language name used to ground the model prompt.</param>
/// <param name="Voice">The neural voice used to synthesize this caller turn.</param>
/// <param name="HasSwitched">Whether the preset's language switch has already taken effect.</param>
public sealed record CallerResponseLanguage(
    string Locale,
    string LanguageName,
    string Voice,
    bool HasSwitched);

/// <summary>
/// Resolves the caller response language deterministically from the number of finalized
/// service-desk transcript turns. Both prompt construction and caller synthesis use this single
/// resolver so the grounded prompt language and the synthesized voice can never drift apart. The
/// model is never asked whether to switch.
/// </summary>
public static class CallerResponseLanguageResolver
{
    /// <summary>
    /// Resolves the response language from a transcript, counting only finalized service-desk
    /// turns. Interim recognition fragments and caller or system turns never trigger the switch.
    /// </summary>
    public static CallerResponseLanguage Resolve(
        CallerScriptSnapshot script,
        IReadOnlyList<TranscriptTurn> transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        return Resolve(script, CountFinalServiceDeskTurns(transcript));
    }

    /// <summary>
    /// Resolves the response language from an already-counted number of finalized service-desk
    /// turns.
    /// </summary>
    public static CallerResponseLanguage Resolve(CallerScriptSnapshot script, int finalServiceDeskTurnCount)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentOutOfRangeException.ThrowIfNegative(finalServiceDeskTurnCount);

        var policy = script.LanguageSwitch;
        if (policy is not null && finalServiceDeskTurnCount >= policy.AfterFinalServiceDeskTurns)
        {
            return new CallerResponseLanguage(
                policy.TargetLocale,
                GetLanguageName(policy.TargetLocale),
                policy.TargetVoice,
                HasSwitched: true);
        }

        return new CallerResponseLanguage(
            script.Locale,
            GetLanguageName(script.Locale),
            script.Voice,
            HasSwitched: false);
    }

    /// <summary>
    /// Counts the finalized service-desk turns that drive the deterministic switch.
    /// </summary>
    public static int CountFinalServiceDeskTurns(IReadOnlyList<TranscriptTurn> transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        var count = 0;
        foreach (var turn in transcript)
        {
            if (turn.Speaker == TranscriptSpeaker.ServiceDesk && turn.Status == TranscriptStatus.Final)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Maps a locale to the human language name used in the grounded prompt.
    /// </summary>
    public static string GetLanguageName(string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);

        if (locale.StartsWith("de", StringComparison.OrdinalIgnoreCase))
        {
            return "German";
        }

        return locale.StartsWith("pl", StringComparison.OrdinalIgnoreCase) ? "Polish" : "English";
    }
}
