using ServiceDeskCallSimulator.Validation;

namespace ServiceDeskCallSimulator.PhoneNumbers;

public static class PhoneNumberSelector
{
    public static PhoneNumberSelectionResult Select(
        IEnumerable<DiscoveredPhoneNumber> discoveredPhoneNumbers,
        string preferredPhoneNumber)
    {
        ArgumentNullException.ThrowIfNull(discoveredPhoneNumbers);
        E164PhoneNumber.EnsureValid(preferredPhoneNumber, nameof(preferredPhoneNumber));

        var outboundNumbers = discoveredPhoneNumbers
            .Where(phoneNumber => phoneNumber.SupportsOutboundCalling)
            .Select(phoneNumber => E164PhoneNumber.EnsureValid(
                phoneNumber.PhoneNumber,
                nameof(DiscoveredPhoneNumber.PhoneNumber)))
            .OrderBy(phoneNumber => phoneNumber, StringComparer.Ordinal)
            .ToArray();

        var selectedPhoneNumber = outboundNumbers.Contains(preferredPhoneNumber, StringComparer.Ordinal)
            ? preferredPhoneNumber
            : null;

        return new PhoneNumberSelectionResult(outboundNumbers, selectedPhoneNumber);
    }
}
