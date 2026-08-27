using ServiceDeskCallSimulator.Callback;

namespace ServiceDeskCallSimulator.Tests;

public sealed class CallbackRouteTests
{
    [Fact]
    public void RandomRoutes_AreNonEmptyDistinctAndComposePublicUris()
    {
        var first = new CallbackRoute();
        var second = new CallbackRoute();
        var publicEndpoint = new Uri("https://example.devtunnels.ms:4321/");

        Assert.NotEqual(first.RouteToken, second.RouteToken);
        Assert.NotEmpty(first.EventPath);
        Assert.NotEmpty(first.MediaPath);
        Assert.NotEqual(first.EventPath, first.MediaPath);

        var eventUri = first.BuildEventUri(publicEndpoint);
        var mediaUri = first.BuildMediaUri(publicEndpoint);

        Assert.Equal("https", eventUri.Scheme);
        Assert.Equal("wss", mediaUri.Scheme);
        Assert.Equal(first.EventPath, eventUri.AbsolutePath);
        Assert.Equal(first.MediaPath, mediaUri.AbsolutePath);
        Assert.Equal(publicEndpoint.Port, eventUri.Port);
        Assert.Equal(publicEndpoint.Port, mediaUri.Port);
    }
}
