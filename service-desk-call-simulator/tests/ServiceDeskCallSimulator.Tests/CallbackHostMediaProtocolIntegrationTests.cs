using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ServiceDeskCallSimulator.Callback;
using ServiceDeskCallSimulator.Media;

namespace ServiceDeskCallSimulator.Tests;

/// <summary>
/// Exercises the real loopback Kestrel callback host and ACS media protocol together without
/// creating a public tunnel or an Azure call.
/// </summary>
public sealed class CallbackHostMediaProtocolIntegrationTests
{
    [Fact]
    public async Task LoopbackHost_RoutesRegisteredEventAndMediaTrafficThroughTheRealSocket()
    {
        await using var host = new CallbackHost(new CallbackHostOptions { Port = 0 });
        await using var transport = new AcsMediaTransport();
        var receivedEvent = new TaskCompletionSource<CallbackEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mediaCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await host.StartAsync();
        await using var registration = host.RegisterCall(
            "active-call",
            (callbackEvent, _) =>
            {
                receivedEvent.TrySetResult(callbackEvent);
                return Task.CompletedTask;
            },
            async (connection, cancellationToken) =>
            {
                try
                {
                    await transport.HandleConnectionAsync(connection.WebSocket, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    mediaCompleted.TrySetResult();
                }
            });

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(host.LocalBaseUri, host.Routes.EventPath));
        request.Headers.Add(CallbackCorrelation.AcsHeaderName, "active-call");
        request.Content = new StringContent(
            """[{"id":"1","type":"Microsoft.Communication.CallConnected","data":{"callConnectionId":"active-call"}}]""",
            Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(request);
        var callbackEvent = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("active-call", callbackEvent.CallConnectionId);

        using var socket = new ClientWebSocket();

        // ACS sends this exact header on the media streaming upgrade. The public media URI has no
        // query string because the call connection ID does not exist yet at CreateCall time.
        socket.Options.SetRequestHeader(CallbackCorrelation.AcsHeaderName, "active-call");
        await socket.ConnectAsync(
            ToWebSocketUri(new Uri(host.LocalBaseUri, host.Routes.MediaPath)),
            CancellationToken.None);

        await SendJsonAsync(
            socket,
            """{"kind":"AudioMetadata","audioMetadata":{"encoding":"pcm","sampleRate":16000,"channels":1}}""");
        await transport.ConnectionReady.WaitAsync(TimeSpan.FromSeconds(2));

        await SendJsonAsync(
            socket,
            """{"kind":"AudioData","audioData":{"data":"AQI=","timestamp":"2026-08-27T00:00:00Z","participantRawId":"service-desk","silent":false}}""");
        var frame = await transport.InboundFrames.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([1, 2], frame.Pcm16KMono.ToArray());
        Assert.Equal("service-desk", frame.ParticipantRawId);
        Assert.False(frame.IsSilent);

        var generation = transport.CreateAudioGeneration();
        await transport.SendAudioAsync(generation, new byte[AcsMediaTransport.PcmFrameBytes]);
        await transport.StopAudioAsync(generation);

        using var outboundAudio = JsonDocument.Parse(await ReceiveTextMessageAsync(socket));
        using var outboundStop = JsonDocument.Parse(await ReceiveTextMessageAsync(socket));
        Assert.Equal("AudioData", outboundAudio.RootElement.GetProperty("kind").GetString());
        Assert.Equal(
            Convert.ToBase64String(new byte[AcsMediaTransport.PcmFrameBytes]),
            outboundAudio.RootElement.GetProperty("audioData").GetProperty("data").GetString());
        Assert.Equal("StopAudio", outboundStop.RootElement.GetProperty("kind").GetString());

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await mediaCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await transport.Disconnected.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, string json)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string> ReceiveTextMessageAsync(ClientWebSocket socket)
    {
        var buffer = new byte[8192];
        var result = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static Uri ToWebSocketUri(Uri uri)
    {
        return new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeWs,
        }.Uri;
    }
}
