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

    public CallerScriptDraft CreateDraft()
    {
        return CallerScriptDraft.FromPreset(this);
    }
}
