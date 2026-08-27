using ServiceDeskCallSimulator.Configuration;
using ServiceDeskCallSimulator.Presets;
using ServiceDeskCallSimulator.Validation;

namespace ServiceDeskCallSimulator.Tests;

public sealed class CallerScriptPresetCatalogTests
{
    [Fact]
    public void CreateDefaultPresets_ReturnsExactNineVariantsWithCorrectVoiceLinkage()
    {
        var settings = new SimulatorSettings();

        var presets = CallerScriptPresetCatalog.CreateDefaultPresets(settings);

        Assert.Equal(9, presets.Count);
        Assert.Equal(
            [
                "[EN] Printer not working",
                "[DE] Drucker funktioniert nicht",
                "[EN] VPN access",
                "[DE] VPN-Zugriff",
                "[EN] Email outage",
                "[DE] E-Mail-Ausfall",
                "[EN] Payroll question",
                "[DE] Frage zur Gehaltsabrechnung",
                "[DE→PL] Netzwerkstörung / awaria sieci",
            ],
            presets.Select(preset => preset.Name).ToArray());

        Assert.All(presets.Where(preset => preset.Name.StartsWith("[EN]")), preset =>
        {
            Assert.Equal(settings.Speech.English.RecognitionLocale, preset.Locale);
            Assert.Equal(settings.Speech.English.Voice, preset.Voice);
        });

        Assert.All(presets.Where(preset => preset.Name.StartsWith("[DE]")), preset =>
        {
            Assert.Equal(settings.Speech.German.RecognitionLocale, preset.Locale);
            Assert.Equal(settings.Speech.German.Voice, preset.Voice);
        });

        Assert.All(presets, preset => Assert.True(E164PhoneNumber.IsValid(preset.CallbackNumber)));
    }

    [Fact]
    public void LanguageSwitchPreset_OpensInGermanAndTargetsPolishAfterTheFirstFinalServiceDeskTurn()
    {
        var settings = new SimulatorSettings();

        var presets = CallerScriptPresetCatalog.CreateDefaultPresets(settings);
        var preset = Assert.Single(presets, item => item.Name == "[DE→PL] Netzwerkstörung / awaria sieci");

        Assert.Equal(settings.Speech.German.RecognitionLocale, preset.Locale);
        Assert.Equal(settings.Speech.German.Voice, preset.Voice);

        var policy = Assert.IsType<CallerLanguageSwitchPolicy>(preset.LanguageSwitch);
        Assert.Equal(settings.Speech.Polish.RecognitionLocale, policy.TargetLocale);
        Assert.Equal(settings.Speech.Polish.Voice, policy.TargetVoice);
        Assert.Equal(1, policy.AfterFinalServiceDeskTurns);

        Assert.All(
            presets.Where(item => item.Name != "[DE→PL] Netzwerkstörung / awaria sieci"),
            item => Assert.Null(item.LanguageSwitch));
    }

    [Fact]
    public void DraftCloneAndSnapshot_CopyTheLanguageSwitchPolicyImmutably()
    {
        var preset = CallerScriptPresetCatalog.CreateDefaultPresets(new SimulatorSettings())
            .Single(item => item.Name == "[DE→PL] Netzwerkstörung / awaria sieci");
        var draft = preset.CreateDraft();
        var clone = draft.Clone();
        var snapshot = CallerScriptSnapshot.FromDraft(draft);

        Assert.Equal(preset.LanguageSwitch, draft.LanguageSwitch);
        Assert.Equal(preset.LanguageSwitch, clone.LanguageSwitch);
        Assert.Equal(preset.LanguageSwitch, snapshot.LanguageSwitch);

        clone.LanguageSwitch = new CallerLanguageSwitchPolicy
        {
            TargetLocale = "fr-FR",
            TargetVoice = "fr-FR-DeniseNeural",
            AfterFinalServiceDeskTurns = 5,
        };
        draft.LanguageSwitch = null;

        Assert.Equal("pl-PL", preset.LanguageSwitch!.TargetLocale);
        Assert.Equal("pl-PL", snapshot.LanguageSwitch!.TargetLocale);
        Assert.Equal(1, snapshot.LanguageSwitch.AfterFinalServiceDeskTurns);
    }

    [Fact]
    public void DraftClone_DoesNotMutatePreset()
    {
        var preset = CallerScriptPresetCatalog.CreateDefaultPresets(new SimulatorSettings()).First();
        var draft = preset.CreateDraft();

        draft.OpeningLine = "Changed opening line";
        draft.CallbackNumber = "+14155559999";

        Assert.NotEqual(draft.OpeningLine, preset.OpeningLine);
        Assert.NotEqual(draft.CallbackNumber, preset.CallbackNumber);
        Assert.Equal("[EN] Printer not working", preset.Name);
        Assert.Equal("+14155550101", preset.CallbackNumber);
    }
}
