using System.Text.Json;

namespace ServiceDeskCallSimulator.DevTunnel;

/// <summary>
/// Describes the outcome of looking up one forwarded port's public URI in <c>devtunnel show --json</c>.
/// </summary>
public enum DevTunnelPortUriStatus
{
    /// <summary>The tunnel does not list the requested port yet; keep polling.</summary>
    PortNotListed,

    /// <summary>The port is listed but has no public URI yet; keep polling.</summary>
    PortUriPending,

    /// <summary>Exactly one valid public URI is available for the requested port.</summary>
    Found,
}

/// <summary>
/// The result of one structured <c>devtunnel show --json</c> port lookup.
/// </summary>
public sealed record DevTunnelPortUriLookup(DevTunnelPortUriStatus Status, Uri? PortUri)
{
    /// <summary>A lookup where the tunnel does not list the port yet.</summary>
    public static DevTunnelPortUriLookup NotListed { get; } = new(DevTunnelPortUriStatus.PortNotListed, null);

    /// <summary>A lookup where the port exists but has no public URI yet.</summary>
    public static DevTunnelPortUriLookup Pending { get; } = new(DevTunnelPortUriStatus.PortUriPending, null);

    /// <summary>A lookup that resolved to exactly one public URI.</summary>
    public static DevTunnelPortUriLookup ForUri(Uri portUri) =>
        new(DevTunnelPortUriStatus.Found, portUri ?? throw new ArgumentNullException(nameof(portUri)));
}

/// <summary>
/// Reads the public URI of one forwarded port out of <c>devtunnel show &lt;id&gt; --json</c>.
/// </summary>
/// <remarks>
/// The Dev Tunnels CLI only publishes a port's public URI once a host is running: neither
/// <c>port create --json</c> nor <c>port show</c> contains it beforehand. Startup therefore starts
/// the host and then polls <c>show --json</c> for this value. The lookup is deliberately
/// structured - it walks <c>tunnel.ports[]</c> and matches <c>portNumber</c> - rather than scanning
/// the document for any URL, so a sibling value such as a tunnel-level or inspection URI can never
/// be mistaken for the callback endpoint.
/// </remarks>
public static class DevTunnelShowParser
{
    /// <summary>
    /// Finds the public URI of <paramref name="portNumber"/> in <c>devtunnel show --json</c> output.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The output is malformed, reports an unusable URL, or reports more than one distinct URL for
    /// the requested port.
    /// </exception>
    public static DevTunnelPortUriLookup FindPortUri(string json, int portNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portNumber);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Dev Tunnels returned malformed JSON output.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Dev Tunnels returned unexpected JSON output.");
            }

            // 'devtunnel show --json' nests the tunnel under "tunnel"; tolerate a flat shape too.
            var tunnel = TryGetProperty(root, "tunnel", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;

            if (!TryGetProperty(tunnel, "ports", out var ports) || ports.ValueKind != JsonValueKind.Array)
            {
                return DevTunnelPortUriLookup.NotListed;
            }

            var portIsListed = false;
            var uris = new HashSet<Uri>();

            foreach (var port in ports.EnumerateArray())
            {
                if (port.ValueKind != JsonValueKind.Object
                    || !TryGetProperty(port, "portNumber", out var number)
                    || number.ValueKind != JsonValueKind.Number
                    || !number.TryGetInt32(out var value)
                    || value != portNumber)
                {
                    continue;
                }

                portIsListed = true;

                if (!TryGetProperty(port, "portUri", out var portUri)
                    || portUri.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var raw = portUri.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
                    || !DevTunnelUrlParser.IsPublicTunnelHttpsUri(uri))
                {
                    throw new InvalidOperationException(
                        $"Dev Tunnels reported an unusable public URL for port {portNumber}.");
                }

                uris.Add(uri);
            }

            if (uris.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Dev Tunnels reported multiple public URLs for port {portNumber}.");
            }

            if (uris.Count == 1)
            {
                return DevTunnelPortUriLookup.ForUri(uris.Single());
            }

            return portIsListed ? DevTunnelPortUriLookup.Pending : DevTunnelPortUriLookup.NotListed;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
