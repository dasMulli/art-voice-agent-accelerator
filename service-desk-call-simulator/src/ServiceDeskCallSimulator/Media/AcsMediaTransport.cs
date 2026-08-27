using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ServiceDeskCallSimulator.Media;

/// <summary>
/// Describes limits for one ACS bidirectional PCM media WebSocket.
/// </summary>
public sealed record AcsMediaTransportOptions
{
    /// <summary>
    /// Gets the maximum assembled text WebSocket message size.
    /// </summary>
    public int MaximumMessageBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// Gets the maximum number of decoded inbound frames retained for consumers.
    /// </summary>
    public int InboundFrameCapacity { get; init; } = 100;

    /// <summary>
    /// Gets the bounded interval used while closing a connected media socket.
    /// </summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Represents one inbound PCM frame delivered by ACS media streaming.
/// </summary>
public sealed record AcsInboundAudioFrame(
    ReadOnlyMemory<byte> Pcm16KMono,
    string Timestamp,
    string? ParticipantRawId,
    bool IsSilent);

/// <summary>
/// Indicates a protocol violation on the ACS media WebSocket.
/// </summary>
public sealed class AcsMediaProtocolException : InvalidOperationException
{
    /// <summary>
    /// Initializes a protocol exception with a safe diagnostic message.
    /// </summary>
    public AcsMediaProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a protocol exception with the underlying parsing error.
    /// </summary>
    public AcsMediaProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Handles the ACS bidirectional media protocol and exposes bounded PCM frames to Task 4.
/// </summary>
public sealed class AcsMediaTransport : ICallMediaTransport, IAsyncDisposable
{
    /// <summary>
    /// Gets the number of bytes in a 20 ms, 16 kHz, 16-bit, mono PCM frame.
    /// </summary>
    public const int PcmFrameBytes = 640;

    private readonly AcsMediaTransportOptions _options;
    private readonly Channel<AcsInboundAudioFrame> _inboundFrames;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _connectionReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disconnected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _socketSync = new();
    private readonly object _closeSync = new();
    private readonly HashSet<long> _stoppedGenerations = [];
    private WebSocket? _socket;
    private Task? _closeTask;
    private int _socketAttached;
    private long _nextGeneration;
    private int _disposed;

    /// <summary>
    /// Initializes the media transport with bounded inbound buffering.
    /// </summary>
    public AcsMediaTransport(AcsMediaTransportOptions? options = null)
    {
        _options = options ?? new AcsMediaTransportOptions();
        ValidateOptions(_options);
        _inboundFrames = Channel.CreateBounded<AcsInboundAudioFrame>(
            new BoundedChannelOptions(_options.InboundFrameCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = true,
            });
    }

    /// <summary>
    /// Completes after ACS supplies validated audio metadata.
    /// </summary>
    public Task ConnectionReady => _connectionReady.Task;

    /// <summary>
    /// Completes after the ACS media socket disconnects or this transport closes it.
    /// </summary>
    public Task Disconnected => _disconnected.Task;

    /// <summary>
    /// Gets the bounded inbound PCM-frame stream for the Task 4 audio pipeline.
    /// </summary>
    public ChannelReader<AcsInboundAudioFrame> InboundFrames => _inboundFrames.Reader;

    /// <summary>
    /// Allocates a monotonic audio generation that Task 4 can cancel independently.
    /// </summary>
    public long CreateAudioGeneration()
    {
        ThrowIfDisposed();
        return Interlocked.Increment(ref _nextGeneration);
    }

    /// <summary>
    /// Runs one accepted ACS media socket until it closes or the session cancels it.
    /// </summary>
    public async Task HandleConnectionAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(socket);
        if (Interlocked.CompareExchange(ref _socketAttached, 1, 0) != 0)
        {
            throw new InvalidOperationException("The ACS media transport already has a socket.");
        }

        lock (_socketSync)
        {
            _socket = socket;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            await ReceiveLoopAsync(socket, linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            // Session cleanup intentionally interrupts a pending WebSocket receive.
        }
        catch (AcsMediaProtocolException)
        {
            await TryCloseWithProtocolErrorAsync(socket).ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_socketSync)
            {
                if (ReferenceEquals(_socket, socket))
                {
                    _socket = null;
                }
            }

            _inboundFrames.Writer.TryComplete();
            _connectionReady.TrySetCanceled();
            _disconnected.TrySetResult();
        }
    }

    /// <summary>
    /// Sends one exact 20 ms PCM frame unless its generation has been stopped.
    /// </summary>
    public async Task SendAudioAsync(
        long generation,
        ReadOnlyMemory<byte> pcm16KMono,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation), "Audio generations must be positive.");
        }

        if (pcm16KMono.Length != PcmFrameBytes)
        {
            throw new ArgumentException(
                $"Outbound PCM frames must be exactly {PcmFrameBytes} bytes (20 ms at 16 kHz PCM mono).",
                nameof(pcm16KMono));
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        await _sendGate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_stoppedGenerations.Contains(generation))
            {
                return;
            }

            var socket = GetOpenSocket();
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "AudioData",
                audioData = new
                {
                    data = Convert.ToBase64String(pcm16KMono.Span),
                },
            });
            await socket.SendAsync(
                payload,
                WebSocketMessageType.Text,
                true,
                operationCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Serializes StopAudio with frames and suppresses future frames from that generation.
    /// </summary>
    public async Task StopAudioAsync(long generation, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation), "Audio generations must be positive.");
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        await _sendGate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_stoppedGenerations.Add(generation))
            {
                return;
            }

            var socket = GetOpenSocket();
            var payload = """{"kind":"StopAudio","stopAudio":{}}"""u8.ToArray();
            await socket.SendAsync(
                payload,
                WebSocketMessageType.Text,
                true,
                operationCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Cancels receive work and closes the media socket without waiting indefinitely.
    /// </summary>
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Task.CompletedTask;
        }

        Task closeTask;
        lock (_closeSync)
        {
            closeTask = _closeTask ??= CloseCoreAsync();
        }

        return AwaitCloseAsync(closeTask, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _disposed, 1);
            _lifetime.Dispose();
            _sendGate.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var receivedMetadata = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            if (message.MessageType != WebSocketMessageType.Text)
            {
                throw new AcsMediaProtocolException("ACS media packets must use text WebSocket messages.");
            }

            using var document = ParseJson(message.Payload);
            var kind = GetRequiredString(document.RootElement, "kind");
            if (!receivedMetadata)
            {
                if (!string.Equals(kind, "AudioMetadata", StringComparison.OrdinalIgnoreCase))
                {
                    throw new AcsMediaProtocolException("The first ACS media packet must be AudioMetadata.");
                }

                ValidateMetadata(document.RootElement);
                receivedMetadata = true;
                _connectionReady.TrySetResult();
                continue;
            }

            if (!string.Equals(kind, "AudioData", StringComparison.OrdinalIgnoreCase))
            {
                throw new AcsMediaProtocolException($"Unexpected ACS media packet kind '{kind}'.");
            }

            _inboundFrames.Writer.TryWrite(ParseAudioData(document.RootElement));
        }
    }

    private async Task<ReceivedMessage?> ReceiveMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            await using var assembled = new MemoryStream();
            WebSocketMessageType? messageType = null;
            while (true)
            {
                var result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await AcknowledgePeerCloseAsync(socket).ConfigureAwait(false);
                    return null;
                }

                messageType ??= result.MessageType;
                if (messageType != result.MessageType)
                {
                    throw new AcsMediaProtocolException("ACS media fragments changed WebSocket message type.");
                }

                if (assembled.Length + result.Count > _options.MaximumMessageBytes)
                {
                    throw new AcsMediaProtocolException("ACS media message exceeded the configured size limit.");
                }

                await assembled.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
                if (result.EndOfMessage)
                {
                    return new ReceivedMessage(messageType.Value, assembled.ToArray());
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static JsonDocument ParseJson(byte[] payload)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new AcsMediaProtocolException("ACS media packet was not valid JSON.", exception);
        }
    }

    private static void ValidateMetadata(JsonElement root)
    {
        var metadata = GetRequiredObject(root, "audioMetadata");
        var encoding = GetRequiredString(metadata, "encoding");
        if (!string.Equals(encoding, "pcm", StringComparison.OrdinalIgnoreCase)
            || GetRequiredInt32(metadata, "sampleRate") != 16_000
            || GetRequiredInt32(metadata, "channels") != 1)
        {
            throw new AcsMediaProtocolException(
                "ACS media metadata must specify PCM, mono, 16,000 Hz audio.");
        }
    }

    private static AcsInboundAudioFrame ParseAudioData(JsonElement root)
    {
        var audioData = GetRequiredObject(root, "audioData");
        var data = GetRequiredString(audioData, "data");
        byte[] pcm;
        try
        {
            pcm = Convert.FromBase64String(data);
        }
        catch (FormatException exception)
        {
            throw new AcsMediaProtocolException("ACS AudioData contained invalid base64 PCM.", exception);
        }

        return new AcsInboundAudioFrame(
            pcm,
            GetRequiredString(audioData, "timestamp"),
            GetOptionalString(audioData, "participantRawId"),
            GetRequiredBoolean(audioData, "silent"));
    }

    private static JsonElement GetRequiredObject(JsonElement element, string propertyName)
    {
        var property = FindProperty(element, propertyName);
        if (property is null || property.Value.Value.ValueKind != JsonValueKind.Object)
        {
            throw new AcsMediaProtocolException($"ACS media packet requires object property '{propertyName}'.");
        }

        return property.Value.Value;
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        var property = FindProperty(element, propertyName);
        if (property is null
            || property.Value.Value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.Value.Value.GetString()))
        {
            throw new AcsMediaProtocolException($"ACS media packet requires string property '{propertyName}'.");
        }

        return property.Value.Value.GetString()!;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        var property = FindProperty(element, propertyName);
        return property is null || property.Value.Value.ValueKind == JsonValueKind.Null
            ? null
            : property.Value.Value.ValueKind == JsonValueKind.String
                ? property.Value.Value.GetString()
                : throw new AcsMediaProtocolException(
                    $"ACS media packet property '{propertyName}' must be a string when present.");
    }

    private static int GetRequiredInt32(JsonElement element, string propertyName)
    {
        var property = FindProperty(element, propertyName);
        if (property is null || !property.Value.Value.TryGetInt32(out var value))
        {
            throw new AcsMediaProtocolException($"ACS media packet requires integer property '{propertyName}'.");
        }

        return value;
    }

    private static bool GetRequiredBoolean(JsonElement element, string propertyName)
    {
        var property = FindProperty(element, propertyName);
        if (property is null || property.Value.Value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new AcsMediaProtocolException($"ACS media packet requires Boolean property '{propertyName}'.");
        }

        return property.Value.Value.GetBoolean();
    }

    private static JsonProperty? FindProperty(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property;
            }
        }

        return null;
    }

    private WebSocket GetOpenSocket()
    {
        lock (_socketSync)
        {
            return _socket is { State: WebSocketState.Open } socket
                ? socket
                : throw new InvalidOperationException("The ACS media WebSocket is not connected.");
        }
    }

    private async Task CloseCoreAsync()
    {
        _lifetime.Cancel();
        WebSocket? socket;
        lock (_socketSync)
        {
            socket = _socket;
        }

        if (socket is not null)
        {
            await CloseSocketOutputAsync(
                socket,
                WebSocketCloseStatus.NormalClosure,
                "Call session ended.").ConfigureAwait(false);
        }

        _inboundFrames.Writer.TryComplete();
        _connectionReady.TrySetCanceled();
        _disconnected.TrySetResult();
    }

    private async Task AcknowledgePeerCloseAsync(WebSocket socket)
    {
        _lifetime.Cancel();
        await CloseSocketOutputAsync(
            socket,
            WebSocketCloseStatus.NormalClosure,
            "ACS media stream closed.").ConfigureAwait(false);
    }

    private async Task TryCloseWithProtocolErrorAsync(WebSocket socket)
    {
        await CloseSocketOutputAsync(
            socket,
            WebSocketCloseStatus.InvalidPayloadData,
            "Invalid ACS media protocol packet.").ConfigureAwait(false);
    }

    private async Task CloseSocketOutputAsync(
        WebSocket socket,
        WebSocketCloseStatus closeStatus,
        string description)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using var closeTimeout = new CancellationTokenSource(_options.CloseTimeout);
        try
        {
            await _sendGate.WaitAsync(closeTimeout.Token).ConfigureAwait(false);
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(
                        closeStatus,
                        description,
                        closeTimeout.Token).WaitAsync(closeTimeout.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                _sendGate.Release();
            }
        }
        catch (OperationCanceledException) when (closeTimeout.IsCancellationRequested)
        {
            socket.Abort();
        }
        catch (WebSocketException)
        {
            socket.Abort();
        }
    }

    private static async Task AwaitCloseAsync(Task closeTask, CancellationToken cancellationToken)
    {
        await closeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateOptions(AcsMediaTransportOptions options)
    {
        if (options.MaximumMessageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The media message limit must be positive.");
        }

        if (options.InboundFrameCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The inbound frame capacity must be positive.");
        }

        if (options.CloseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The media close timeout must be positive.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private sealed record ReceivedMessage(WebSocketMessageType MessageType, byte[] Payload);
}
