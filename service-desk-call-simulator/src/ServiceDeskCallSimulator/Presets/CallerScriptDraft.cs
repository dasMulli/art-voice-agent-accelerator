namespace ServiceDeskCallSimulator.Presets;

public sealed class CallerScriptDraft
{
    public string Name { get; set; } = string.Empty;

    public string Locale { get; set; } = string.Empty;

    public string Voice { get; set; } = string.Empty;

    public string OpeningLine { get; set; } = string.Empty;

    public string Identity { get; set; } = string.Empty;

    public string Background { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string Urgency { get; set; } = string.Empty;

    public string CallbackNumber { get; set; } = string.Empty;

    public string AdditionalDetails { get; set; } = string.Empty;

    public static CallerScriptDraft FromPreset(CallerScriptPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        return new CallerScriptDraft
        {
            Name = preset.Name,
            Locale = preset.Locale,
            Voice = preset.Voice,
            OpeningLine = preset.OpeningLine,
            Identity = preset.Identity,
            Background = preset.Background,
            Reason = preset.Reason,
            Urgency = preset.Urgency,
            CallbackNumber = preset.CallbackNumber,
            AdditionalDetails = preset.AdditionalDetails,
        };
    }

    public CallerScriptDraft Clone()
    {
        return new CallerScriptDraft
        {
            Name = Name,
            Locale = Locale,
            Voice = Voice,
            OpeningLine = OpeningLine,
            Identity = Identity,
            Background = Background,
            Reason = Reason,
            Urgency = Urgency,
            CallbackNumber = CallbackNumber,
            AdditionalDetails = AdditionalDetails,
        };
    }
}
