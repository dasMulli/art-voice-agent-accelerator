using System.Security.Cryptography;

namespace ServiceDeskCallSimulator.Callback;

/// <summary>
/// Defines the unguessable callback paths used by one simulator process.
/// </summary>
public sealed class CallbackRoute
{
    private const int TokenByteLength = 32;

    /// <summary>
    /// Creates a route using a cryptographically secure per-process token.
    /// </summary>
    public CallbackRoute()
        : this(CreateToken())
    {
    }

    /// <summary>
    /// Creates a route from a token. This overload is intended for deterministic tests.
    /// </summary>
    public CallbackRoute(string routeToken)
    {
        if (string.IsNullOrWhiteSpace(routeToken)
            || routeToken.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("The callback route token must be a non-empty base64url value.", nameof(routeToken));
        }

        RouteToken = routeToken;
        EventPath = $"/callbacks/{RouteToken}/events";
        MediaPath = $"/callbacks/{RouteToken}/media";
    }

    /// <summary>
    /// Gets the random token shared by this process's event and media routes.
    /// </summary>
    public string RouteToken { get; }

    /// <summary>
    /// Gets the relative HTTPS event callback path.
    /// </summary>
    public string EventPath { get; }

    /// <summary>
    /// Gets the relative WSS media callback path.
    /// </summary>
    public string MediaPath { get; }

    /// <summary>
    /// Builds the public HTTPS event URI from a Dev Tunnel forwarding endpoint.
    /// </summary>
    public Uri BuildEventUri(Uri publicHttpsEndpoint) => BuildUri(publicHttpsEndpoint, EventPath, Uri.UriSchemeHttps);

    /// <summary>
    /// Builds the public WSS media URI from a Dev Tunnel forwarding endpoint.
    /// </summary>
    public Uri BuildMediaUri(Uri publicHttpsEndpoint) => BuildUri(publicHttpsEndpoint, MediaPath, Uri.UriSchemeWss);

    private static Uri BuildUri(Uri publicHttpsEndpoint, string path, string scheme)
    {
        ArgumentNullException.ThrowIfNull(publicHttpsEndpoint);

        if (!publicHttpsEndpoint.IsAbsoluteUri
            || !string.Equals(publicHttpsEndpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The public endpoint must be an absolute HTTPS URI.", nameof(publicHttpsEndpoint));
        }

        return new UriBuilder(publicHttpsEndpoint)
        {
            Scheme = scheme,
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
    }

    private static string CreateToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenByteLength))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
