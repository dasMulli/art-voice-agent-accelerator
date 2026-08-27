namespace ServiceDeskCallSimulator.Presets;

/// <summary>
/// An optional, first-class caller language switch. The switch is a declared preset fact: it is
/// never inferred from free-text script details and never decided by the caller model. The
/// simulator applies it deterministically once the remote service desk has completed
/// <see cref="AfterFinalServiceDeskTurns"/> finalized transcript turns.
/// </summary>
public sealed record class CallerLanguageSwitchPolicy
{
    /// <summary>
    /// Gets the locale the caller responds in after the switch, for example <c>pl-PL</c>.
    /// </summary>
    public required string TargetLocale { get; init; }

    /// <summary>
    /// Gets the neural voice used for caller synthesis after the switch.
    /// </summary>
    public required string TargetVoice { get; init; }

    /// <summary>
    /// Gets the number of finalized service-desk turns after which the caller switches language.
    /// </summary>
    public required int AfterFinalServiceDeskTurns { get; init; }

    /// <summary>
    /// Returns an independent copy so drafts, snapshots, and presets never share mutable state.
    /// </summary>
    public CallerLanguageSwitchPolicy Copy() => this with { };

    /// <summary>
    /// Returns a validated copy, or throws when any declared value is unusable.
    /// </summary>
    public CallerLanguageSwitchPolicy Validated(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(TargetLocale))
        {
            throw new ArgumentException("The language switch target locale must not be blank.", fieldName);
        }

        if (string.IsNullOrWhiteSpace(TargetVoice))
        {
            throw new ArgumentException("The language switch target voice must not be blank.", fieldName);
        }

        if (AfterFinalServiceDeskTurns < 1)
        {
            throw new ArgumentException(
                "The language switch threshold must be at least one finalized service-desk turn.",
                fieldName);
        }

        return new CallerLanguageSwitchPolicy
        {
            TargetLocale = TargetLocale.Trim(),
            TargetVoice = TargetVoice.Trim(),
            AfterFinalServiceDeskTurns = AfterFinalServiceDeskTurns,
        };
    }
}
