using ServiceDeskCallSimulator.Configuration;
using ServiceDeskCallSimulator.Presets;
using ServiceDeskCallSimulator.Validation;

namespace ServiceDeskCallSimulator.Tests;

public sealed class CallerScriptPresetCatalogTests
{
    [Fact]
    public void CreateDefaultPresets_ReturnsExactEightVariantsWithCorrectVoiceLinkage()
    {
        var settings = new SimulatorSettings();

        var presets = CallerScriptPresetCatalog.CreateDefaultPresets(settings);

        Assert.Equal(8, presets.Count);
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
