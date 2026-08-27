using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ServiceDeskCallSimulator.Configuration;

namespace ServiceDeskCallSimulator.Tests;

public sealed class SimulatorConfigurationTests
{
    [Fact]
    public void LoadSettings_UsesJsonDefaults()
    {
        var directory = CreateConfigurationDirectory();

        var settings = SimulatorConfiguration.LoadSettingsFrom(
            directory,
            environmentVariablePrefix: null);

        Assert.Equal("https://acs-ai-demos.europe.communication.azure.com", settings.Acs.Endpoint);
        Assert.Equal("rg-demos", settings.Acs.ResourceGroup);
        Assert.Equal("acs-ai-demos", settings.Acs.ResourceName);
        Assert.Equal("+43800223359", settings.Acs.PreferredCallerId);
        Assert.Equal("+33801150311", settings.Acs.DefaultDestination);
        Assert.Equal(0, settings.Acs.LocalCallbackPort);

        Assert.Equal("https://aif-demos-swedencentral.cognitiveservices.azure.com/", settings.AiServices.Endpoint);
        Assert.Equal("gpt-5.6-luna", settings.AiServices.TextDeployment);

        Assert.Equal("en-US", settings.Speech.English.RecognitionLocale);
        Assert.Equal("en-US-JennyNeural", settings.Speech.English.Voice);
        Assert.Equal("de-DE", settings.Speech.German.RecognitionLocale);
        Assert.Equal("de-DE-KatjaNeural", settings.Speech.German.Voice);

        DeleteDirectory(directory);
    }

    [Fact]
    public void LoadSettings_EnvironmentOverridesJson()
    {
        const string envKey = "SDCS__Acs__PreferredCallerId";
        var previous = Environment.GetEnvironmentVariable(envKey);
        var directory = CreateConfigurationDirectory();

        try
        {
            Environment.SetEnvironmentVariable(envKey, "+49999888777");

            var settings = SimulatorConfiguration.LoadSettingsFrom(directory);

            Assert.Equal("+49999888777", settings.Acs.PreferredCallerId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, previous);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void LoadSettings_InvalidValuesFailValidationWithActionableFieldNames()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Acs:Endpoint"] = "http://not-https",
                ["Acs:ResourceGroup"] = "",
                ["Acs:ResourceName"] = "",
                ["Acs:PreferredCallerId"] = "123",
                ["Acs:DefaultDestination"] = "abc",
                ["Acs:LocalCallbackPort"] = "70000",
                ["AiServices:Endpoint"] = "not-a-uri",
                ["AiServices:TextDeployment"] = "",
                ["Speech:English:RecognitionLocale"] = "",
                ["Speech:English:Voice"] = "",
                ["Speech:German:RecognitionLocale"] = "",
                ["Speech:German:Voice"] = "",
            })
            .Build();

        var exception = Assert.Throws<OptionsValidationException>(
            () => SimulatorConfiguration.LoadSettings(configuration));

        Assert.Contains(exception.Failures, failure => failure.Contains("Acs.Endpoint", StringComparison.Ordinal));
        Assert.Contains(exception.Failures, failure => failure.Contains("Acs.PreferredCallerId", StringComparison.Ordinal));
        Assert.Contains(exception.Failures, failure => failure.Contains("Acs.DefaultDestination", StringComparison.Ordinal));
        Assert.Contains(exception.Failures, failure => failure.Contains("AiServices.Endpoint", StringComparison.Ordinal));
        Assert.Contains(exception.Failures, failure => failure.Contains("Speech.English.Voice", StringComparison.Ordinal));
        Assert.Contains(exception.Failures, failure => failure.Contains("Speech.German.RecognitionLocale", StringComparison.Ordinal));
    }

    private static string CreateConfigurationDirectory()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "config-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "appsettings.json"),
            """
            {
              "Acs": {
                "Endpoint": "https://acs-ai-demos.europe.communication.azure.com",
                "ResourceGroup": "rg-demos",
                "ResourceName": "acs-ai-demos",
                "PreferredCallerId": "+43800223359",
                "DefaultDestination": "+33801150311",
                "LocalCallbackPort": 0
              },
              "AiServices": {
                "Endpoint": "https://aif-demos-swedencentral.cognitiveservices.azure.com/",
                "TextDeployment": "gpt-5.6-luna"
              },
              "Speech": {
                "English": {
                  "RecognitionLocale": "en-US",
                  "Voice": "en-US-JennyNeural"
                },
                "German": {
                  "RecognitionLocale": "de-DE",
                  "Voice": "de-DE-KatjaNeural"
                }
              }
            }
            """);

        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
