using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Core;

namespace ServiceDeskCallSimulator.Calls;

/// <summary>
/// Specifies the ACS media streaming settings required for an outbound call.
/// </summary>
public sealed record AcsMediaStreamingRequest(
    Uri TransportUri,
    bool StartMediaStreaming,
    bool EnableBidirectional,
    bool EnableDtmfTones);

/// <summary>
/// Specifies the tested, non-SDK input for an ACS create-call operation.
/// </summary>
public sealed record AcsCreateCallRequest(
    string SourcePhoneNumber,
    string DestinationPhoneNumber,
    Uri CallbackUri,
    AcsMediaStreamingRequest MediaStreaming);

/// <summary>
/// Reports the create-call event obtained through the SDK event processor.
/// </summary>
public sealed record AcsCreateCallEvent(bool IsSuccess, string? FailureReason);

/// <summary>
/// Represents the result of creating an ACS call and its early-event processor.
/// </summary>
public sealed record AcsCallCreation(
    string CallConnectionId,
    Func<CancellationToken, Task<AcsCreateCallEvent>> WaitForInitialEventAsync);

/// <summary>
/// Represents the single SDK call control operation needed by an active session.
/// </summary>
public interface ICallConnectionHandle
{
    /// <summary>
    /// Hangs up the P2P call for every participant.
    /// </summary>
    Task HangUpAsync(bool forEveryone, CancellationToken cancellationToken);
}

/// <summary>
/// Restricts the ACS SDK boundary to create, obtain-connection, and hang-up operations.
/// </summary>
public interface ICallAutomationGateway
{
    /// <summary>
    /// Starts an outbound ACS call.
    /// </summary>
    Task<AcsCallCreation> CreateCallAsync(AcsCreateCallRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Obtains a call connection handle for a call returned by <see cref="CreateCallAsync"/>.
    /// </summary>
    ICallConnectionHandle GetCallConnection(string callConnectionId);
}

/// <summary>
/// Production implementation of the narrow ACS call automation boundary.
/// </summary>
public sealed class AcsCallAutomationGateway : ICallAutomationGateway
{
    private readonly CallAutomationClient _client;

    /// <summary>
    /// Initializes the gateway with the shared Entra credential.
    /// </summary>
    public AcsCallAutomationGateway(Uri endpoint, TokenCredential credential)
        : this(new CallAutomationClient(
            endpoint ?? throw new ArgumentNullException(nameof(endpoint)),
            credential ?? throw new ArgumentNullException(nameof(credential))))
    {
        Endpoint = endpoint;
    }

    /// <summary>
    /// Initializes the gateway with an existing SDK client.
    /// </summary>
    public AcsCallAutomationGateway(CallAutomationClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>
    /// Gets the ACS endpoint when the gateway constructed its SDK client.
    /// </summary>
    public Uri? Endpoint { get; }

    /// <inheritdoc />
    public async Task<AcsCallCreation> CreateCallAsync(
        AcsCreateCallRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = await _client.CreateCallAsync(CreateSdkOptions(request), cancellationToken).ConfigureAwait(false);
        var result = response.Value;
        var callConnectionId = result.CallConnectionProperties.CallConnectionId;
        if (string.IsNullOrWhiteSpace(callConnectionId))
        {
            throw new InvalidOperationException("ACS returned a create-call response without a call connection ID.");
        }

        return new AcsCallCreation(
            callConnectionId,
            async eventCancellationToken =>
            {
                var eventResult = await result.WaitForEventProcessorAsync(eventCancellationToken).ConfigureAwait(false);
                return eventResult.IsSuccess
                    ? new AcsCreateCallEvent(true, null)
                    : new AcsCreateCallEvent(
                        false,
                        eventResult.FailureResult?.ResultInformation?.Message);
            });
    }

    /// <inheritdoc />
    public ICallConnectionHandle GetCallConnection(string callConnectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callConnectionId);
        return new AcsCallConnectionHandle(_client.GetCallConnection(callConnectionId));
    }

    /// <summary>
    /// Creates SDK options that exactly configure bidirectional 16 kHz PCM media streaming.
    /// </summary>
    public static CreateCallOptions CreateSdkOptions(AcsCreateCallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.MediaStreaming);

        var invite = new CallInvite(
            new PhoneNumberIdentifier(request.DestinationPhoneNumber),
            new PhoneNumberIdentifier(request.SourcePhoneNumber));
        return new CreateCallOptions(invite, request.CallbackUri)
        {
            MediaStreamingOptions = new MediaStreamingOptions(
                MediaStreamingAudioChannel.Unmixed,
                StreamingTransport.Websocket)
            {
                TransportUri = request.MediaStreaming.TransportUri,
                MediaStreamingContent = MediaStreamingContent.Audio,
                StartMediaStreaming = request.MediaStreaming.StartMediaStreaming,
                EnableBidirectional = request.MediaStreaming.EnableBidirectional,
                EnableDtmfTones = request.MediaStreaming.EnableDtmfTones,
                AudioFormat = AudioFormat.Pcm16KMono,
            },
        };
    }

    private sealed class AcsCallConnectionHandle : ICallConnectionHandle
    {
        private readonly CallConnection _connection;

        public AcsCallConnectionHandle(CallConnection connection)
        {
            _connection = connection;
        }

        public async Task HangUpAsync(bool forEveryone, CancellationToken cancellationToken)
        {
            await _connection.HangUpAsync(forEveryone, cancellationToken).ConfigureAwait(false);
        }
    }
}
