namespace ServiceDeskCallSimulator.PhoneNumbers;

public sealed record class DiscoveredPhoneNumber(
    string PhoneNumber,
    bool SupportsOutboundCalling);
