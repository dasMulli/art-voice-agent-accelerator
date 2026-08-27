namespace ServiceDeskCallSimulator.Presets;

public sealed record class CallerScriptPreset
{
    public required string Name { get; init; }

    public required string Locale { get; init; }

    public required string Voice { get; init; }

    public required string OpeningLine { get; init; }

    public required string Identity { get; init; }

    public required string Background { get; init; }

    public required string Reason { get; init; }

    public required string Urgency { get; init; }

    public required string CallbackNumber { get; init; }

    public required string AdditionalDetails { get; init; }

    /// <summary>
    /// Gets the optional deterministic language switch applied during the call, or <c>null</c>
    /// when the caller keeps <see cref="Locale"/> and <see cref="Voice"/> for the whole call.
    /// </summary>
    public CallerLanguageSwitchPolicy? LanguageSwitch { get; init; }

    public CallerScriptDraft CreateDraft()
    {
        return CallerScriptDraft.FromPreset(this);
    }
}
