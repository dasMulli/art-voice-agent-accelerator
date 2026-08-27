using System.Text.Json;
using System.Text.RegularExpressions;

namespace ServiceDeskCallSimulator.DevTunnel;

/// <summary>
/// Parses a Dev Tunnels HTTPS forwarding endpoint without accepting arbitrary URLs from CLI diagnostics.
/// </summary>
/// <remarks>
/// Startup no longer uses these parsers: the live CLI publishes a port's public URI only through
/// <c>devtunnel show --json</c> once a host is running (see <see cref="DevTunnelShowParser"/>), and
/// the redirected <c>devtunnel host</c> text produced no captured output on the target environment.
/// They are retained as compatibility/diagnostic helpers for host output that some CLI versions do
/// emit, and <see cref="IsPublicTunnelHttpsUri"/> is the shared endpoint-validation rule used by
/// the structured parser.
/// </remarks>
public static partial class DevTunnelUrlParser
{
    /// <summary>
    /// Extracts one HTTPS forwarding endpoint from JSON emitted by a command that supports <c>--json</c>.
    /// </summary>
    public static Uri? ParseHttpsForwardingUrlFromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            var candidates = new HashSet<Uri>();
            FindHttpsUris(document.RootElement, candidates);
            return SelectSingleCandidate(candidates, "JSON output");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Dev Tunnels returned malformed JSON output.", exception);
        }
    }

    /// <summary>
    /// Extracts one HTTPS forwarding endpoint from <c>devtunnel host</c> output, which has no JSON mode.
    /// </summary>
    public static Uri ParseHttpsForwardingUrlFromHostOutput(string output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(output);

        var candidates = HostUrlRegex()
            .Matches(output)
            .Select(match => match.Value.TrimEnd('/', '.', ',', ';', ')', ']'))
            .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri is not null)
            .Cast<Uri>()
            .ToHashSet();

        return SelectSingleCandidate(candidates, "Dev Tunnels host output")
            ?? throw new InvalidOperationException("Dev Tunnels host output did not contain an HTTPS forwarding URL.");
    }

    private static void FindHttpsUris(JsonElement element, ISet<Uri> candidates)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    FindHttpsUris(property.Value, candidates);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    FindHttpsUris(item, candidates);
                }

                break;
            case JsonValueKind.String:
                if (Uri.TryCreate(element.GetString(), UriKind.Absolute, out var uri) && IsTunnelHttpsUri(uri))
                {
                    candidates.Add(uri);
                }

                break;
        }
    }

    private static Uri? SelectSingleCandidate(ISet<Uri> candidates, string source)
    {
        return candidates.Count switch
        {
            0 => null,
            1 => candidates.Single(),
            _ => throw new InvalidOperationException($"{source} contained multiple HTTPS forwarding URLs."),
        };
    }

    /// <summary>
    /// Determines whether a URI is an acceptable public Dev Tunnels HTTPS endpoint. This is the
    /// single validation rule shared by every Dev Tunnels endpoint parser.
    /// </summary>
    internal static bool IsPublicTunnelHttpsUri(Uri uri) => IsTunnelHttpsUri(uri);

    private static bool IsTunnelHttpsUri(Uri uri)
    {
        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && (uri.Host.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".tunnels.api.visualstudio.com", StringComparison.OrdinalIgnoreCase))
            && uri.AbsolutePath == "/"
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }

    [GeneratedRegex(
        @"https://[a-z0-9][a-z0-9.-]*\.(?:devtunnels\.ms|tunnels\.api\.visualstudio\.com)(?::\d+)?/?(?![a-z0-9_/-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostUrlRegex();
}
