using System.Text.RegularExpressions;

namespace ServiceDeskCallSimulator.Validation;

public static partial class E164PhoneNumber
{
    public static bool IsValid(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && E164Pattern().IsMatch(value);
    }

    public static string EnsureValid(string value, string parameterName)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                "The value must be a valid E.164 phone number in the form + followed by 2-15 digits.",
                parameterName);
        }

        return value;
    }

    [GeneratedRegex(@"^\+[1-9]\d{1,14}$", RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex E164Pattern();
}
