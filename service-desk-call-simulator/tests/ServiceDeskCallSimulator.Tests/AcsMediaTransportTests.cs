using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ServiceDeskCallSimulator.Media;

namespace ServiceDeskCallSimulator.Tests;

public sealed class AcsMediaTransportTests
{
    [Fact]
    public async Task ValidMetadataAndAudioData_AreAcceptedAndPublished()
    {
        var socket = new ScriptedWebSocket();
        socket.EnqueueText(Metadata());
        socket.EnqueueText(AudioData("AQI=", "2026-08-26T18:00:00Z", "participant-1", false));
        socket.EnqueueClose();
        await using var transport = new AcsMediaTransport();

        await transport.HandleConnectionAsync(socket, CancellationToken.None);
        var frame = await transport.InboundFrames.ReadAsync();

        Assert.Equal([1, 2], frame.Pcm16KMono.ToArray());
        Assert.Equal("2026-08-26T18:00:00Z", frame.Timestamp);
        Assert.Equal("participant-1", frame.ParticipantRawId);
        Assert.False(frame.IsSilent);
        await transport.ConnectionReady;
        await transport.Disconnected;
    }

    [Fact]
    public async Task FragmentedTextPacket_IsReassembled()
    {
        var socket = new ScriptedWebSocket();
        var metadata = Metadata();
        socket.EnqueueText(metadata[..20], endOfMessage: false);
        socket.EnqueueText(metadata[20..]);
        socket.EnqueueText(AudioData("AQ==", "1", null, true));
        socket.EnqueueClose();
        await using var transport = new AcsMediaTransport();

        await transport.HandleConnectionAsync(socket, CancellationToken.None);
        var frame = await transport.InboundFrames.ReadAsync();

        Assert.Equal([1], frame.Pcm16KMono.ToArray());
        Assert.True(frame.IsSilent);
    }

    [Theory]
    [InlineData("""{"kind":"AudioMetadata","audioMetadata":{"encoding":"wav","sampleRate":16000,"channels":1}}""")]
    [InlineData("""{"kind":"AudioMetadata","audioMetadata":{"encoding":"pcm","sampleRate":8000,"channels":1}}""")]
    [InlineData("""{"kind":"AudioMetadata","audioMetadata":{"encoding":"pcm","sampleRate":16000,"channels":2}}""")]
    public async Task IncompatibleMetadata_IsRejected(string metadata)
    {
        var socket = new ScriptedWebSocket();
        socket.EnqueueText(metadata);
        await using var transport = new AcsMediaTransport();

        await Assert.ThrowsAsync<AcsMediaProtocolException>(
            () => transport.HandleConnectionAsync(socket, CancellationToken.None));

        Assert.Equal(WebSocketCloseStatus.InvalidPayloadData, socket.CloseStatus);
    }

    [Fact]
    public async Task MalformedOversizeBinaryAndBase64Packets_AreRejected()
    {
        await AssertInvalidAsync(socket => socket.EnqueueText("{not-json"));
        await AssertInvalidAsync(socket =>
        {
            socket.EnqueueText(Metadata());
            socket.EnqueueText(AudioData("not base64!", "1", null, false));
        });
        await AssertInvalidAsync(socket => socket.EnqueueBinary([1, 2, 3]));

        var oversizedSocket = new ScriptedWebSocket();
        oversizedSocket.EnqueueText(Metadata());
        await using var oversizedTransport = new AcsMediaTransport(new AcsMediaTransportOptions
        {
            MaximumMessageBytes = 20,
        });
        await Assert.ThrowsAsync<AcsMediaProtocolException>(
            () => oversizedTransport.HandleConnectionAsync(oversizedSocket, CancellationToken.None));
    }

    [Fact]
    public async Task InboundFrameBuffer_DropsOldestWhenAtCapacity()
    {
        var socket = new ScriptedWebSocket();
        socket.EnqueueText(Metadata());
        socket.EnqueueText(AudioData("AQ==", "1", null, false));
        socket.EnqueueText(AudioData("Ag==", "2", null, false));
        socket.EnqueueText(AudioData("Aw==", "3", null, false));
        socket.EnqueueClose();
        await using var transport = new AcsMediaTransport(new AcsMediaTransportOptions
        {
            InboundFrameCapacity = 2,
        });

        await transport.HandleConnectionAsync(socket, CancellationToken.None);
        var frames = new List<AcsInboundAudioFrame>();
        while (transport.InboundFrames.TryRead(out var frame))
        {
            frames.Add(frame);
        }

        Assert.Equal(2, frames.Count);
        Assert.Equal([2], frames[0].Pcm16KMono.ToArray());
        Assert.Equal([3], frames[1].Pcm16KMono.ToArray());
    }

    [Fact]
    public async Task OutboundAudio_RequiresExactFrameAndUsesAcsSchema()
    {
        var socket = new ScriptedWebSocket();
        socket.EnqueueText(Metadata());
        await using var transport = new AcsMediaTransport();
        var handler = transport.HandleConnectionAsync(socket, CancellationToken.None);
        await transport.ConnectionReady.WaitAsync(TimeSpan.FromSeconds(2));
        var generation = transport.CreateAudioGeneration();

        await Assert.ThrowsAsync<ArgumentException>(() => transport.SendAudioAsync(generation, new byte[639]));
        await transport.SendAudioAsync(generation, new byte[AcsMediaTransport.PcmFrameBytes]);
        await transport.StopAudioAsync(generation);

        var messages = socket.SentMessages;
        Assert.Equal(2, messages.Count);
        using var audioJson = JsonDocument.Parse(messages[0]);
        Assert.Equal("AudioData", audioJson.RootElement.GetProperty("kind").GetString());
        Assert.Equal(
            Convert.ToBase64String(new byte[AcsMediaTransport.PcmFrameBytes]),
            audioJson.RootElement.GetProperty("audioData").GetProperty("data").GetString());
        using var stopJson = JsonDocument.Parse(messages[1]);
        Assert.Equal("StopAudio", stopJson.RootElement.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Object, stopJson.RootElement.GetProperty("stopAudio").ValueKind);

        socket.EnqueueClose();
        await handler;
    }

    [Fact]
    public async Task StopAudio_SerializesWithFramesAndSuppressesStaleGeneration()
    {
        var socket = new ScriptedWebSocket { BlockFirstSend = true };
        socket.EnqueueText(Metadata());
        await using var transport = new AcsMediaTransport();
        var handler = transport.HandleConnectionAsync(socket, CancellationToken.None);
        await transport.ConnectionReady.WaitAsync(TimeSpan.FromSeconds(2));
        var generation = transport.CreateAudioGeneration();

        var sending = transport.SendAudioAsync(generation, new byte[AcsMediaTransport.PcmFrameBytes]);
        await socket.FirstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopping = transport.StopAudioAsync(generation);
        Assert.False(stopping.IsCompleted);
        socket.ReleaseFirstSend();
        await Task.WhenAll(sending, stopping);
        await transport.SendAudioAsync(generation, new byte[AcsMediaTransport.PcmFrameBytes]);

        Assert.Equal(1, socket.MaximumConcurrentSends);
        Assert.Equal(2, socket.SentMessages.Count);
        using var first = JsonDocument.Parse(socket.SentMessages[0]);
        using var second = JsonDocument.Parse(socket.SentMessages[1]);
        Assert.Equal("AudioData", first.RootElement.GetProperty("kind").GetString());
        Assert.Equal("StopAudio", second.RootElement.GetProperty("kind").GetString());

        socket.EnqueueClose();
        await handler;
    }

    [Fact]
    public async Task CloseAsync_CancelsReceiveAndClosesMediaSocket()
    {
        var socket = new ScriptedWebSocket();
        socket.EnqueueText(Metadata());
        await using var transport = new AcsMediaTransport();
        var handler = transport.HandleConnectionAsync(socket, CancellationToken.None);
        await transport.ConnectionReady.WaitAsync(TimeSpan.FromSeconds(2));

        await transport.CloseAsync();
        await handler.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(socket.CloseOutputCalled);
        await transport.Disconnected;
    }

    [Fact]
    public async Task PeerClose_IsAcknowledgedWithinTheCloseBound()
    {
        var socket = new ScriptedWebSocket();
        socket.EnqueueText(Metadata());
        socket.EnqueueClose();
        await using var transport = new AcsMediaTransport(new AcsMediaTransportOptions
        {
            CloseTimeout = TimeSpan.FromSeconds(1),
        });

        await transport.HandleConnectionAsync(socket, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(socket.CloseOutputCalled);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
        await transport.Disconnected;
    }

    [Fact]
    public async Task CloseAsync_CancelsBlockedSendBeforeClosingThroughTheSendGate()
    {
        var socket = new ScriptedWebSocket { BlockFirstSend = true };
        socket.EnqueueText(Metadata());
        await using var transport = new AcsMediaTransport(new AcsMediaTransportOptions
        {
            CloseTimeout = TimeSpan.FromSeconds(1),
        });
        var handler = transport.HandleConnectionAsync(socket, CancellationToken.None);
        await transport.ConnectionReady.WaitAsync(TimeSpan.FromSeconds(2));
        var generation = transport.CreateAudioGeneration();
        var sending = transport.SendAudioAsync(generation, new byte[AcsMediaTransport.PcmFrameBytes]);
        await socket.FirstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await transport.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await handler.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(socket.CloseOutputCalled);
        Assert.Equal(1, socket.MaximumConcurrentSends);
    }

    private static async Task AssertInvalidAsync(Action<ScriptedWebSocket> arrange)
    {
        var socket = new ScriptedWebSocket();
        arrange(socket);
        await using var transport = new AcsMediaTransport();

        await Assert.ThrowsAsync<AcsMediaProtocolException>(
            () => transport.HandleConnectionAsync(socket, CancellationToken.None));
    }

    private static string Metadata() =>
        """{"kind":"AudioMetadata","audioMetadata":{"encoding":"pcm","sampleRate":16000,"channels":1}}""";

    private static string AudioData(string data, string timestamp, string? participantRawId, bool silent) =>
        JsonSerializer.Serialize(
            new
            {
                kind = "aUdIoDaTa",
                audioData = new
                {
                    data,
                    timestamp,
                    participantRawId,
                    silent,
                },
            });

    private sealed class ScriptedWebSocket : WebSocket
    {
        private readonly Queue<ReceiveStep> _received = new();
        private readonly TaskCompletionSource _receivePending =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstSendRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sendSync = new();
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;
        private int _activeSends;
        private int _sendCount;

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;

        public override string? CloseStatusDescription => _closeStatusDescription;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public bool BlockFirstSend { get; init; }

        public bool CloseOutputCalled { get; private set; }

        public TaskCompletionSource FirstSendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<byte[]> SentMessages { get; } = [];

        public int MaximumConcurrentSends { get; private set; }

        public void EnqueueText(string payload, bool endOfMessage = true) =>
            _received.Enqueue(new ReceiveStep(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(payload), endOfMessage));

        public void EnqueueBinary(byte[] payload) =>
            _received.Enqueue(new ReceiveStep(WebSocketMessageType.Binary, payload, true));

        public void EnqueueClose()
        {
            _received.Enqueue(new ReceiveStep(WebSocketMessageType.Close, [], true));
            _receivePending.TrySetResult();
        }

        public void ReleaseFirstSend() => _firstSendRelease.TrySetResult();

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            _receivePending.TrySetResult();
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) =>
            CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            CloseOutputCalled = true;
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            _receivePending.TrySetResult();
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _receivePending.TrySetResult();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (_received.Count == 0)
            {
                await _receivePending.Task.WaitAsync(cancellationToken);
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            var step = _received.Dequeue();
            step.Payload.AsSpan().CopyTo(buffer.AsSpan());
            if (step.MessageType == WebSocketMessageType.Close)
            {
                _state = WebSocketState.CloseReceived;
            }

            return new WebSocketReceiveResult(step.Payload.Length, step.MessageType, step.EndOfMessage);
        }

        public override async Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            Assert.Equal(WebSocketMessageType.Text, messageType);
            Assert.True(endOfMessage);
            var sendIndex = Interlocked.Increment(ref _sendCount);
            var active = Interlocked.Increment(ref _activeSends);
            lock (_sendSync)
            {
                MaximumConcurrentSends = Math.Max(MaximumConcurrentSends, active);
                SentMessages.Add(buffer.ToArray());
            }

            try
            {
                if (BlockFirstSend && sendIndex == 1)
                {
                    FirstSendStarted.TrySetResult();
                    await _firstSendRelease.Task.WaitAsync(cancellationToken);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeSends);
            }
        }

        private sealed record ReceiveStep(WebSocketMessageType MessageType, byte[] Payload, bool EndOfMessage);
    }
}
