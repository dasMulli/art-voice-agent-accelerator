using ServiceDeskCallSimulator.Validation;

namespace ServiceDeskCallSimulator.Tests;

public sealed class E164PhoneNumberTests
{
    [Theory]
    [InlineData("+12")]
    [InlineData("+123456789012345")]
    [InlineData("+49876543210")]
    public void IsValid_AcceptsExpectedValues(string value)
    {
        Assert.True(E164PhoneNumber.IsValid(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("12345")]
    [InlineData("+1")]
    [InlineData("+012")]
    [InlineData("+12 34")]
    [InlineData("+12-34")]
    [InlineData("++123")]
    [InlineData("+1234567890123456")]
    public void IsValid_RejectsMalformedValues(string? value)
    {
        Assert.False(E164PhoneNumber.IsValid(value));
    }
}
