using ServiceDeskCallSimulator.PhoneNumbers;

namespace ServiceDeskCallSimulator.Tests;

public sealed class PhoneNumberSelectorTests
{
    [Fact]
    public void Select_RejectsMalformedOutboundSourceNumbers()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            PhoneNumberSelector.Select(
                [
                    new DiscoveredPhoneNumber("+3312345678901", true),
                    new DiscoveredPhoneNumber("+43800223359", true),
                    new DiscoveredPhoneNumber("+43 800223359", true),
                ],
                "+43800223359"));

        Assert.Equal("PhoneNumber", exception.ParamName);
    }

    [Fact]
    public void Select_FiltersOutboundNumbersAndOrdersDeterministically()
    {
        var result = PhoneNumberSelector.Select(
            [
                new DiscoveredPhoneNumber("+4412345678900", true),
                new DiscoveredPhoneNumber("+3312345678901", true),
                new DiscoveredPhoneNumber("+4412345678999", false),
                new DiscoveredPhoneNumber("+43800223359", true),
            ],
            "+43800223359");

        Assert.Equal(
            [
                "+3312345678901",
                "+43800223359",
                "+4412345678900",
            ],
            result.OutboundNumbers);
        Assert.Equal("+43800223359", result.SelectedPhoneNumber);
    }

    [Fact]
    public void Select_ReturnsNoDefaultSelectionWhenPreferredNumberIsMissing()
    {
        var result = PhoneNumberSelector.Select(
            [
                new DiscoveredPhoneNumber("+3312345678901", true),
                new DiscoveredPhoneNumber("+4412345678900", true),
            ],
            "+43800223359");

        Assert.Equal(
            [
                "+3312345678901",
                "+4412345678900",
            ],
            result.OutboundNumbers);
        Assert.Null(result.SelectedPhoneNumber);
    }
}
