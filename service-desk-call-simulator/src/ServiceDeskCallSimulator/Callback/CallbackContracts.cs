using System.Net.WebSockets;

namespace ServiceDeskCallSimulator.Callback;

/// <summary>
/// Receives a raw event callback after its call connection ID has been authenticated against an active registration.
/// </summary>
public delegate Task CallbackEventHandler(CallbackEvent callbackEvent, CancellationToken cancellationToken);

/// <summary>
/// Receives an accepted media WebSocket after its call connection ID has been authenticated against an active registration.
/// </summary>
public delegate Task MediaConnectionHandler(MediaConnection connection, CancellationToken cancellationToken);

/// <summary>
/// Represents a callback event without taking a dependency on future ACS event SDK types.
/// </summary>
public sealed record CallbackEvent(string CallConnectionId, ReadOnlyMemory<byte> Body, string? ContentType);

/// <summary>
/// Represents an accepted media connection without taking a dependency on future ACS media SDK types.
/// </summary>
public sealed record MediaConnection(string CallConnectionId, WebSocket WebSocket);

/// <summary>
/// Defines the correlation value expected on callbacks and media connections.
/// </summary>
public static class CallbackCorrelation
{
    /// <summary>
    /// Gets the query-string key Task 3 should append to callback and media URIs.
    /// </summary>
    public const string QueryParameterName = "callConnectionId";

    /// <summary>
    /// Gets the header Azure Communication Services sends on the media streaming WebSocket
    /// upgrade request. Media URIs cannot carry a query string because the call connection ID
    /// does not exist yet when the transport URI is supplied to CreateCall.
    /// </summary>
    public const string AcsHeaderName = "x-ms-call-connection-id";

    /// <summary>
    /// Gets the additional header alias accepted from local tooling and tests when neither the
    /// query-string value nor the ACS header is available.
    /// </summary>
    public const string HeaderName = "X-Call-Connection-Id";

    /// <summary>
    /// Gets every accepted correlation header name. Header lookups are case-insensitive.
    /// </summary>
    public static IReadOnlyList<string> HeaderNames { get; } = [AcsHeaderName, HeaderName];
}
