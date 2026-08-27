namespace ServiceDeskCallSimulator.PhoneNumbers;

public sealed record class PhoneNumberSelectionResult(
    IReadOnlyList<string> OutboundNumbers,
    string? SelectedPhoneNumber);
