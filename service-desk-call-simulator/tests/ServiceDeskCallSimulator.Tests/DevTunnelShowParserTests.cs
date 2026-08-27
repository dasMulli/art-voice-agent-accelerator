using ServiceDeskCallSimulator.DevTunnel;

namespace ServiceDeskCallSimulator.Tests;

/// <summary>
/// Covers the structured <c>devtunnel show &lt;id&gt; --json</c> port lookup that replaced the
/// unreliable host-text scraping: matching port selection, pending states, and the malformed,
/// ambiguous, and unusable-URI rejections.
/// </summary>
public sealed class DevTunnelShowParserTests
{
    private const string TwoPortsJson = """
        {
          "tunnel": {
            "tunnelId": "sdcs-abc",
            "clusterId": "euw",
            "tunnelUri": "https://sdcs-abc.euw.devtunnels.ms/",
            "ports": [
              { "portNumber": 5001, "protocol": "http", "portUri": "https://sdcs-abc-5001.euw.devtunnels.ms/" },
              { "portNumber": 5002, "protocol": "http", "portUri": "https://sdcs-abc-5002.euw.devtunnels.ms/" }
            ]
          }
        }
        """;

    [Fact]
    public void FindPortUri_ReturnsTheUriOfTheMatchingPortOnly()
    {
        var first = DevTunnelShowParser.FindPortUri(TwoPortsJson, 5001);
        var second = DevTunnelShowParser.FindPortUri(TwoPortsJson, 5002);

        Assert.Equal(DevTunnelPortUriStatus.Found, first.Status);
        Assert.Equal("https://sdcs-abc-5001.euw.devtunnels.ms/", first.PortUri!.AbsoluteUri);
        Assert.Equal(DevTunnelPortUriStatus.Found, second.Status);
        Assert.Equal("https://sdcs-abc-5002.euw.devtunnels.ms/", second.PortUri!.AbsoluteUri);
    }

    [Fact]
    public void FindPortUri_IgnoresTunnelLevelAndInspectionUris()
    {
        const string json = """
            {"tunnel":{"tunnelUri":"https://sdcs-abc.euw.devtunnels.ms/","ports":[
              {"portNumber":5001,"portUri":"https://sdcs-abc-5001.euw.devtunnels.ms/",
               "inspectionUri":"https://sdcs-abc-5001-inspect.euw.devtunnels.ms/"}]}}
            """;

        var lookup = DevTunnelShowParser.FindPortUri(json, 5001);

        Assert.Equal(DevTunnelPortUriStatus.Found, lookup.Status);
        Assert.Equal("https://sdcs-abc-5001.euw.devtunnels.ms/", lookup.PortUri!.AbsoluteUri);
    }

    [Fact]
    public void FindPortUri_AcceptsAFlatTunnelShapeAndIsPropertyNameCaseInsensitive()
    {
        const string json = """
            {"TunnelId":"sdcs-abc","Ports":[{"PortNumber":5001,"PortUri":"https://sdcs-abc-5001.euw.devtunnels.ms/"}]}
            """;

        var lookup = DevTunnelShowParser.FindPortUri(json, 5001);

        Assert.Equal(DevTunnelPortUriStatus.Found, lookup.Status);
        Assert.Equal("https://sdcs-abc-5001.euw.devtunnels.ms/", lookup.PortUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("""{"tunnel":{"tunnelId":"sdcs-abc"}}""")]
    [InlineData("""{"tunnel":{"tunnelId":"sdcs-abc","ports":[]}}""")]
    [InlineData("""{"tunnel":{"ports":[{"portNumber":9999,"portUri":"https://x-9999.euw.devtunnels.ms/"}]}}""")]
    public void FindPortUri_ReportsPortNotListedBeforeTheTunnelPublishesIt(string json)
    {
        var lookup = DevTunnelShowParser.FindPortUri(json, 5001);

        Assert.Equal(DevTunnelPortUriStatus.PortNotListed, lookup.Status);
        Assert.Null(lookup.PortUri);
    }

    [Theory]
    [InlineData("""{"tunnel":{"ports":[{"portNumber":5001,"protocol":"http"}]}}""")]
    [InlineData("""{"tunnel":{"ports":[{"portNumber":5001,"portUri":""}]}}""")]
    [InlineData("""{"tunnel":{"ports":[{"portNumber":5001,"portUri":null}]}}""")]
    public void FindPortUri_ReportsPendingWhileTheListedPortHasNoPublicUriYet(string json)
    {
        var lookup = DevTunnelShowParser.FindPortUri(json, 5001);

        Assert.Equal(DevTunnelPortUriStatus.PortUriPending, lookup.Status);
        Assert.Null(lookup.PortUri);
    }

    [Fact]
    public void FindPortUri_RejectsMalformedJson()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => DevTunnelShowParser.FindPortUri("{not json", 5001));

        Assert.Contains("malformed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    public void FindPortUri_RejectsUnexpectedJsonShapes(string json)
    {
        Assert.Throws<InvalidOperationException>(() => DevTunnelShowParser.FindPortUri(json, 5001));
    }

    [Fact]
    public void FindPortUri_RejectsAmbiguousUrisForTheSamePort()
    {
        const string json = """
            {"tunnel":{"ports":[
              {"portNumber":5001,"portUri":"https://one-5001.euw.devtunnels.ms/"},
              {"portNumber":5001,"portUri":"https://two-5001.euw.devtunnels.ms/"}]}}
            """;

        var exception = Assert.Throws<InvalidOperationException>(
            () => DevTunnelShowParser.FindPortUri(json, 5001));

        Assert.Contains("multiple public URLs", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindPortUri_AcceptsARepeatedIdenticalUriForTheSamePort()
    {
        const string json = """
            {"tunnel":{"ports":[
              {"portNumber":5001,"portUri":"https://one-5001.euw.devtunnels.ms/"},
              {"portNumber":5001,"portUri":"https://one-5001.euw.devtunnels.ms/"}]}}
            """;

        var lookup = DevTunnelShowParser.FindPortUri(json, 5001);

        Assert.Equal(DevTunnelPortUriStatus.Found, lookup.Status);
    }

    [Theory]
    [InlineData("http://sdcs-abc-5001.euw.devtunnels.ms/")]      // not HTTPS
    [InlineData("https://evil.example.com/")]                    // not a Dev Tunnels host
    [InlineData("https://sdcs-abc-5001.euw.devtunnels.ms/path")] // not a bare origin
    [InlineData("https://sdcs-abc-5001.euw.devtunnels.ms/?q=1")] // carries a query
    [InlineData("not-a-uri")]
    public void FindPortUri_RejectsUnusablePortUris(string portUri)
    {
        var json = $$$"""{"tunnel":{"ports":[{"portNumber":5001,"portUri":"{{{portUri}}}"}]}}""";

        var exception = Assert.Throws<InvalidOperationException>(
            () => DevTunnelShowParser.FindPortUri(json, 5001));

        Assert.Contains("unusable public URL", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FindPortUri_RejectsEmptyOutput(string json)
    {
        Assert.Throws<ArgumentException>(() => DevTunnelShowParser.FindPortUri(json, 5001));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FindPortUri_RejectsNonPositivePortNumbers(int portNumber)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DevTunnelShowParser.FindPortUri(TwoPortsJson, portNumber));
    }
}
