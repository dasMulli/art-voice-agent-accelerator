using ServiceDeskCallSimulator.Conversation;
using ServiceDeskCallSimulator.Presets;

namespace ServiceDeskCallSimulator.Tests;

/// <summary>
/// Covers the deterministic caller response language policy. The switch is decided in code from
/// the count of finalized service-desk turns and never by the model.
/// </summary>
public sealed class CallerResponseLanguageResolverTests
{
    [Fact]
    public void Resolve_BeforeThreshold_KeepsTheInitialGermanLocaleAndVoice()
    {
        var script = CreateSwitchingScript();

        var language = CallerResponseLanguageResolver.Resolve(script, finalServiceDeskTurnCount: 0);

        Assert.Equal("de-DE", language.Locale);
        Assert.Equal("de-DE-KatjaNeural", language.Voice);
        Assert.Equal("German", language.LanguageName);
        Assert.False(language.HasSwitched);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public void Resolve_AtOrAfterThreshold_SwitchesToPolish(int finalServiceDeskTurnCount)
    {
        var script = CreateSwitchingScript();

        var language = CallerResponseLanguageResolver.Resolve(script, finalServiceDeskTurnCount);

        Assert.Equal("pl-PL", language.Locale);
        Assert.Equal("pl-PL-ZofiaNeural", language.Voice);
        Assert.Equal("Polish", language.LanguageName);
        Assert.True(language.HasSwitched);
    }

    [Fact]
    public void Resolve_WithoutPolicy_NeverSwitches()
    {
        var script = CreateSwitchingScript() with { LanguageSwitch = null };

        var language = CallerResponseLanguageResolver.Resolve(script, finalServiceDeskTurnCount: 9);

        Assert.Equal("de-DE", language.Locale);
        Assert.Equal("de-DE-KatjaNeural", language.Voice);
        Assert.False(language.HasSwitched);
    }

    [Fact]
    public void Resolve_CountsOnlyFinalServiceDeskTurns()
    {
        var script = CreateSwitchingScript();
        var transcript = new[]
        {
            Turn(TranscriptSpeaker.Caller, "Guten Tag, hier ist Maya.", TranscriptStatus.Final),
            Turn(TranscriptSpeaker.ServiceDesk, "Welches", TranscriptStatus.Interim),
            Turn(TranscriptSpeaker.System, "Recognition started.", TranscriptStatus.Final),
        };

        var beforeFinal = CallerResponseLanguageResolver.Resolve(script, transcript);

        Assert.Equal(0, CallerResponseLanguageResolver.CountFinalServiceDeskTurns(transcript));
        Assert.False(beforeFinal.HasSwitched);
        Assert.Equal("de-DE", beforeFinal.Locale);

        var afterFinal = CallerResponseLanguageResolver.Resolve(
            script,
            [.. transcript, Turn(TranscriptSpeaker.ServiceDesk, "Welches Gerät?", TranscriptStatus.Final)]);

        Assert.True(afterFinal.HasSwitched);
        Assert.Equal("pl-PL", afterFinal.Locale);
        Assert.Equal("pl-PL-ZofiaNeural", afterFinal.Voice);
    }

    [Theory]
    [InlineData("", "pl-PL-ZofiaNeural", 1)]
    [InlineData("pl-PL", "  ", 1)]
    [InlineData("pl-PL", "pl-PL-ZofiaNeural", 0)]
    [InlineData("pl-PL", "pl-PL-ZofiaNeural", -1)]
    public void Snapshot_RejectsInvalidLanguageSwitchPolicies(
        string targetLocale,
        string targetVoice,
        int afterFinalServiceDeskTurns)
    {
        var draft = CreateSwitchingDraft();
        draft.LanguageSwitch = new CallerLanguageSwitchPolicy
        {
            TargetLocale = targetLocale,
            TargetVoice = targetVoice,
            AfterFinalServiceDeskTurns = afterFinalServiceDeskTurns,
        };

        Assert.Throws<ArgumentException>(() => CallerScriptSnapshot.FromDraft(draft));
    }

    [Fact]
    public void LanguageName_IsResolvedFromTheLocalePrefix()
    {
        Assert.Equal("German", CallerResponseLanguageResolver.GetLanguageName("de-DE"));
        Assert.Equal("Polish", CallerResponseLanguageResolver.GetLanguageName("pl-PL"));
        Assert.Equal("English", CallerResponseLanguageResolver.GetLanguageName("en-US"));
    }

    private static TranscriptTurn Turn(TranscriptSpeaker speaker, string text, TranscriptStatus status) =>
        new(DateTimeOffset.Parse("2026-08-27T09:00:00Z"), speaker, text, status);

    private static CallerScriptSnapshot CreateSwitchingScript() =>
        CallerScriptSnapshot.FromDraft(CreateSwitchingDraft());

    private static CallerScriptDraft CreateSwitchingDraft() => new()
    {
        Name = "[DE→PL] Netzwerkstörung / awaria sieci",
        Locale = "de-DE",
        Voice = "de-DE-KatjaNeural",
        OpeningLine = "Guten Tag, hier ist Maya.",
        Identity = "Maya",
        Background = "Das Standortnetz ist ausgefallen.",
        Reason = "Wir brauchen eine Störungsmeldung.",
        Urgency = "Hoch",
        CallbackNumber = "+4915112345682",
        AdditionalDetails = "Rückruf jederzeit möglich.",
        LanguageSwitch = new CallerLanguageSwitchPolicy
        {
            TargetLocale = "pl-PL",
            TargetVoice = "pl-PL-ZofiaNeural",
            AfterFinalServiceDeskTurns = 1,
        },
    };
}
