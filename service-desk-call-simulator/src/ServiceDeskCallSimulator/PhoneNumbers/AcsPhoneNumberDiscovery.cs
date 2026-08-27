using Azure.Communication.PhoneNumbers;
using ServiceDeskCallSimulator.Validation;

namespace ServiceDeskCallSimulator.PhoneNumbers;

public sealed class AcsPhoneNumberDiscovery
{
    private readonly PhoneNumbersClient _phoneNumbersClient;
    private readonly string _preferredCallerId;

    public AcsPhoneNumberDiscovery(
        PhoneNumbersClient phoneNumbersClient,
        string preferredCallerId)
    {
        _phoneNumbersClient = phoneNumbersClient ?? throw new ArgumentNullException(nameof(phoneNumbersClient));
        _preferredCallerId = E164PhoneNumber.EnsureValid(preferredCallerId, nameof(preferredCallerId));
    }

    public async Task<PhoneNumberSelectionResult> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var purchasedNumbers = new List<DiscoveredPhoneNumber>();

        await foreach (var phoneNumber in _phoneNumbersClient.GetPurchasedPhoneNumbersAsync(cancellationToken))
        {
            purchasedNumbers.Add(Map(phoneNumber));
        }

        return PhoneNumberSelector.Select(purchasedNumbers, _preferredCallerId);
    }

    private static DiscoveredPhoneNumber Map(PurchasedPhoneNumber phoneNumber)
    {
        var validatedPhoneNumber = E164PhoneNumber.EnsureValid(
            phoneNumber.PhoneNumber,
            nameof(PurchasedPhoneNumber.PhoneNumber));

        return new DiscoveredPhoneNumber(
            validatedPhoneNumber,
            SupportsOutboundCalling(phoneNumber.Capabilities.Calling));
    }

    private static bool SupportsOutboundCalling(PhoneNumberCapabilityType capabilityType)
    {
        return capabilityType == PhoneNumberCapabilityType.Outbound
            || capabilityType == PhoneNumberCapabilityType.InboundOutbound;
    }
}
