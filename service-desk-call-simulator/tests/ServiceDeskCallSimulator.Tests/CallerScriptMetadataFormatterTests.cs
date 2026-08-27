using ServiceDeskCallSimulator.Configuration;
using ServiceDeskCallSimulator.Presets;
using ServiceDeskCallSimulator.UI;

namespace ServiceDeskCallSimulator.Tests;

/// <summary>
/// Covers the read-only locale/voice metadata rendered next to the caller script. The formatter is
/// a pure function so the UI expectation is verified without creating any window handle.
/// </summary>
public sealed class CallerScriptMetadataFormatterTests
{
    [Fact]
    public void OrdinaryPreset_ShowsPlainLocaleAndVoice()
    {
        var draft = CallerScriptPresetCatalog.CreateDefaultPresets(new SimulatorSettings())
            .Single(preset => preset.Name == "[DE] Drucker funktioniert nicht")
            .CreateDraft();

        Assert.Equal("de-DE", CallerScriptMetadataFormatter.FormatLocale(draft));
        Assert.Equal("de-DE-KatjaNeural", CallerScriptMetadataFormatter.FormatVoice(draft));
    }

    [Fact]
    public void LanguageSwitchPreset_ShowsTheTransitionAndThreshold()
    {
        var draft = CallerScriptPresetCatalog.CreateDefaultPresets(new SimulatorSettings())
            .Single(preset => preset.Name == "[DE→PL] Netzwerkstörung / awaria sieci")
            .CreateDraft();

        Assert.Equal(
            "de-DE → pl-PL (after 1 service desk turn)",
            CallerScriptMetadataFormatter.FormatLocale(draft));
        Assert.Equal(
            "de-DE-KatjaNeural → pl-PL-ZofiaNeural (after 1 service desk turn)",
            CallerScriptMetadataFormatter.FormatVoice(draft));
    }

    [Fact]
    public void MultiTurnThreshold_IsPluralized()
    {
        var draft = CallerScriptPresetCatalog.CreateDefaultPresets(new SimulatorSettings())
            .Single(preset => preset.Name == "[DE→PL] Netzwerkstörung / awaria sieci")
            .CreateDraft();
        draft.LanguageSwitch = draft.LanguageSwitch! with { AfterFinalServiceDeskTurns = 3 };

        Assert.Equal(
            "de-DE → pl-PL (after 3 service desk turns)",
            CallerScriptMetadataFormatter.FormatLocale(draft));
    }

    [Fact]
    public void NullDraft_RendersEmptyMetadata()
    {
        Assert.Equal(string.Empty, CallerScriptMetadataFormatter.FormatLocale(null));
        Assert.Equal(string.Empty, CallerScriptMetadataFormatter.FormatVoice(null));
    }

    [Fact]
    public void DraftComparer_TreatsTheLanguageSwitchPolicyAsAnEditableFact()
    {
        var draft = CallerScriptPresetCatalog.CreateDefaultPresets(new SimulatorSettings())
            .Single(preset => preset.Name == "[DE→PL] Netzwerkstörung / awaria sieci")
            .CreateDraft();
        var clone = draft.Clone();

        Assert.True(CallerScriptDraftComparer.AreEqual(draft, clone));

        clone.LanguageSwitch = null;

        Assert.False(CallerScriptDraftComparer.AreEqual(draft, clone));
    }
}
