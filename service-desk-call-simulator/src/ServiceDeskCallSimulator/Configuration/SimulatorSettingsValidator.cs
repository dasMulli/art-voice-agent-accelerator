using Microsoft.Extensions.Options;
using ServiceDeskCallSimulator.Validation;

namespace ServiceDeskCallSimulator.Configuration;

public static class SimulatorSettingsValidator
{
    public static void ValidateAndThrow(SimulatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var failures = new List<string>();

        ValidateHttpsUri(settings.Acs.Endpoint, $"{nameof(SimulatorSettings.Acs)}.{nameof(AcsSettings.Endpoint)}", failures);
        ValidateNonEmpty(settings.Acs.ResourceGroup, $"{nameof(SimulatorSettings.Acs)}.{nameof(AcsSettings.ResourceGroup)}", failures);
        ValidateNonEmpty(settings.Acs.ResourceName, $"{nameof(SimulatorSettings.Acs)}.{nameof(AcsSettings.ResourceName)}", failures);
        ValidateE164(settings.Acs.PreferredCallerId, $"{nameof(SimulatorSettings.Acs)}.{nameof(AcsSettings.PreferredCallerId)}", failures);
        ValidateE164(settings.Acs.DefaultDestination, $"{nameof(SimulatorSettings.Acs)}.{nameof(AcsSettings.DefaultDestination)}", failures);
        ValidatePort(settings.Acs.LocalCallbackPort, $"{nameof(SimulatorSettings.Acs)}.{nameof(AcsSettings.LocalCallbackPort)}", failures);

        ValidateHttpsUri(settings.AiServices.Endpoint, $"{nameof(SimulatorSettings.AiServices)}.{nameof(AiServicesSettings.Endpoint)}", failures);
        ValidateNonEmpty(settings.AiServices.TextDeployment, $"{nameof(SimulatorSettings.AiServices)}.{nameof(AiServicesSettings.TextDeployment)}", failures);

        ValidateNonEmpty(settings.Speech.English.RecognitionLocale, $"{nameof(SimulatorSettings.Speech)}.{nameof(SpeechSettings.English)}.{nameof(SpeechLocaleSettings.RecognitionLocale)}", failures);
        ValidateNonEmpty(settings.Speech.English.Voice, $"{nameof(SimulatorSettings.Speech)}.{nameof(SpeechSettings.English)}.{nameof(SpeechLocaleSettings.Voice)}", failures);
        ValidateNonEmpty(settings.Speech.German.RecognitionLocale, $"{nameof(SimulatorSettings.Speech)}.{nameof(SpeechSettings.German)}.{nameof(SpeechLocaleSettings.RecognitionLocale)}", failures);
        ValidateNonEmpty(settings.Speech.German.Voice, $"{nameof(SimulatorSettings.Speech)}.{nameof(SpeechSettings.German)}.{nameof(SpeechLocaleSettings.Voice)}", failures);

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(nameof(SimulatorSettings), typeof(SimulatorSettings), failures);
        }
    }

    private static void ValidateE164(string value, string fieldName, ICollection<string> failures)
    {
        if (!E164PhoneNumber.IsValid(value))
        {
            failures.Add($"{fieldName} must be a valid E.164 phone number.");
        }
    }

    private static void ValidateHttpsUri(string value, string fieldName, ICollection<string> failures)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{fieldName} must be an absolute https:// URI.");
        }
    }

    private static void ValidateNonEmpty(string value, string fieldName, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{fieldName} must not be empty.");
        }
    }

    private static void ValidatePort(int value, string fieldName, ICollection<string> failures)
    {
        if (value is < 0 or > 65535)
        {
            failures.Add($"{fieldName} must be between 0 and 65535.");
        }
    }
}
