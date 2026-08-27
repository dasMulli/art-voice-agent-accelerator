namespace ServiceDeskCallSimulator.Configuration;

public sealed record class SimulatorSettings
{
    public const string ConfigurationPrefix = "SDCS__";

    public AcsSettings Acs { get; init; } = new();

    public AiServicesSettings AiServices { get; init; } = new();

    public SpeechSettings Speech { get; init; } = new();
}

public sealed record class AcsSettings
{
    public string Endpoint { get; init; } = "https://acs-ai-demos.europe.communication.azure.com";

    public string ResourceGroup { get; init; } = "rg-demos";

    public string ResourceName { get; init; } = "acs-ai-demos";

    public string PreferredCallerId { get; init; } = "+43800223359";

    public string DefaultDestination { get; init; } = "+33801150311";

    public int LocalCallbackPort { get; init; } = 0;
}

public sealed record class AiServicesSettings
{
    public string Endpoint { get; init; } = "https://aif-demos-swedencentral.cognitiveservices.azure.com/";

    public string TextDeployment { get; init; } = "gpt-5.6-luna";
}

public sealed record class SpeechLocaleSettings
{
    public string RecognitionLocale { get; init; } = string.Empty;

    public string Voice { get; init; } = string.Empty;
}

public sealed record class SpeechSettings
{
    public SpeechLocaleSettings English { get; init; } = new()
    {
        RecognitionLocale = "en-US",
        Voice = "en-US-JennyNeural",
    };

    public SpeechLocaleSettings German { get; init; } = new()
    {
        RecognitionLocale = "de-DE",
        Voice = "de-DE-KatjaNeural",
    };

    public SpeechLocaleSettings Polish { get; init; } = new()
    {
        RecognitionLocale = "pl-PL",
        Voice = "pl-PL-ZofiaNeural",
    };
}
