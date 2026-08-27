using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Diagnostics;
using ServiceDeskCallSimulator.Callback;

namespace ServiceDeskCallSimulator.Tests;

public sealed class CallbackHostTests
{
    [Fact]
    public async Task EphemeralHost_DispatchesOnlyRegisteredEvents()
    {
        await using var host = new CallbackHost(new CallbackHostOptions { Port = 0 });
        var received = new TaskCompletionSource<CallbackEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await host.StartAsync();
        await using var registration = host.RegisterCall(
            "active-call",
            (callbackEvent, _) =>
            {
                received.TrySetResult(callbackEvent);
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask);
        using var client = new HttpClient();

        var validResponse = await client.PostAsync(
            WithCallId(host.LocalBaseUri, host.Routes.EventPath, "active-call"),
            new StringContent("""{"event":"connected"}""", Encoding.UTF8, "application/json"));
        var callbackEvent = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var staleResponse = await client.PostAsync(
            WithCallId(host.LocalBaseUri, host.Routes.EventPath, "stale-call"),
            new StringContent("{}", Encoding.UTF8, "application/json"));
        var wrongRouteResponse = await client.PostAsync(
            new Uri(host.LocalBaseUri, "/callbacks/wrong/events"),
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Accepted, validResponse.StatusCode);
        Assert.Equal("active-call", callbackEvent.CallConnectionId);
        Assert.Equal("""{"event":"connected"}""", Encoding.UTF8.GetString(callbackEvent.Body.Span));
        Assert.Equal(HttpStatusCode.NotFound, staleResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongRouteResponse.StatusCode);
        Assert.True(host.BoundPort > 0);
        Assert.Equal(IPAddress.Loopback, IPAddress.Parse(host.LocalBaseUri.Host));
    }

    [Fact]
    public async Task MediaEndpoint_AcceptsOnlyRegisteredWebSocketConnections()
    {
        await using var host = new CallbackHost();
        var connected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await host.StartAsync();
        await using var registration = host.RegisterCall(
            "active-call",
            (_, _) => Task.CompletedTask,
            async (connection, cancellationToken) =>
            {
                connected.TrySetResult(connection.CallConnectionId);
                await connection.WebSocket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "test complete",
                    cancellationToken);
            });

        using var validSocket = new ClientWebSocket();
        await validSocket.ConnectAsync(
            ToWebSocketUri(WithCallId(host.LocalBaseUri, host.Routes.MediaPath, "active-call")),
            CancellationToken.None);
        Assert.Equal("active-call", await connected.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        using var unknownSocket = new ClientWebSocket();
        var exception = await Assert.ThrowsAsync<WebSocketException>(() => unknownSocket.ConnectAsync(
            ToWebSocketUri(WithCallId(host.LocalBaseUri, host.Routes.MediaPath, "unknown-call")),
            CancellationToken.None));
        Assert.NotEqual(WebSocketError.Success, exception.WebSocketErrorCode);

        using var client = new HttpClient();
        var nonWebSocketResponse = await client.GetAsync(new Uri(host.LocalBaseUri, host.Routes.MediaPath));
        Assert.Equal(HttpStatusCode.BadRequest, nonWebSocketResponse.StatusCode);
    }

    [Fact]
    public async Task EventCallbacks_AreNotBlockedByAnActiveMediaSocket()
    {
        await using var host = new CallbackHost();
        var mediaEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMedia = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await host.StartAsync();
        await using var registration = host.RegisterCall(
            "active-call",
            (_, _) =>
            {
                eventReceived.TrySetResult();
                return Task.CompletedTask;
            },
            async (_, cancellationToken) =>
            {
                mediaEntered.TrySetResult();
                await releaseMedia.Task.WaitAsync(cancellationToken);
            });
        using var mediaSocket = new ClientWebSocket();
        await mediaSocket.ConnectAsync(
            ToWebSocketUri(WithCallId(host.LocalBaseUri, host.Routes.MediaPath, "active-call")),
            CancellationToken.None);
        await mediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var client = new HttpClient();

        var response = await client.PostAsync(
            WithCallId(host.LocalBaseUri, host.Routes.EventPath, "active-call"),
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await eventReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseMedia.TrySetResult();
    }

    [Fact]
    public async Task EventPayloadCorrelation_RejectsAmbiguousCallIds()
    {
        await using var host = new CallbackHost();
        await host.StartAsync();
        await using var registration = host.RegisterCall(
            "active-call",
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
        using var client = new HttpClient();

        var response = await client.PostAsync(
            new Uri(host.LocalBaseUri, host.Routes.EventPath),
            new StringContent(
                """[{"data":{"callConnectionId":"active-call"}},{"data":{"callConnectionId":"stale-call"}}]""",
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EventEndpoint_RejectsRequestAndPayloadCorrelationDisagreement()
    {
        await using var host = new CallbackHost();
        await host.StartAsync();
        await using var registration = host.RegisterCall(
            "active-call",
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
        using var client = new HttpClient();

        var response = await client.PostAsync(
            WithCallId(host.LocalBaseUri, host.Routes.EventPath, "active-call"),
            new StringContent(
                """{"data":{"callConnectionId":"stale-call"}}""",
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("x-ms-call-connection-id")]
    [InlineData("X-MS-Call-Connection-Id")]
    [InlineData("X-Call-Connection-Id")]
    public async Task MediaEndpoint_AcceptsTheAcsCorrelationHeaderWithoutAQueryString(string headerName)
    {
        await using var host = new CallbackHost();
        var connected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await host.StartAsync();
        await using var registration = host.RegisterCall(
            "active-call",
            (_, _) => Task.CompletedTask,
            async (connection, cancellationToken) =>
            {
                connected.TrySetResult(connection.CallConnectionId);
                await connection.WebSocket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "test complete",
                    cancellationToken);
            });

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(headerName, "active-call");
        var mediaUri = ToWebSocketUri(new Uri(host.LocalBaseUri, host.Routes.MediaPath));
        await socket.ConnectAsync(mediaUri, CancellationToken.None);

        Assert.Equal(string.Empty, mediaUri.Query);
        Assert.Equal("active-call", await connected.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task MediaEndpoint_RejectsAnUnknownAcsCorrelationHeader()
    {
        await using var host = new CallbackHost();
        await host.StartAsync();
        await using var registration = host.RegisterCall(
            "active-call",
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(CallbackCorrelation.AcsHeaderName, "unknown-call");

        var exception = await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(
            ToWebSocketUri(new Uri(host.LocalBaseUri, host.Routes.MediaPath)),
            CancellationToken.None));

        Assert.NotEqual(WebSocketError.Success, exception.WebSocketErrorCode);
    }

    [Fact]
    public async Task MediaEndpoint_FailsClosedWhenNoCorrelationValueIsSupplied()
    {
        await using var host = new CallbackHost();
        await host.StartAsync();
        await using var registration = host.RegisterCall(
            "active-call",
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);

        using var socket = new ClientWebSocket();

        var exception = await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(
            ToWebSocketUri(new Uri(host.LocalBaseUri, host.Routes.MediaPath)),
            CancellationToken.None));

        Assert.NotEqual(WebSocketError.Success, exception.WebSocketErrorCode);
    }

    [Fact]
    public async Task MediaEndpoint_RejectsDisagreeingCorrelationHeaders()
    {
        await using var host = new CallbackHost();
        await host.StartAsync();
        await using var registration = host.RegisterCall(
            "active-call",
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(CallbackCorrelation.AcsHeaderName, "active-call");
        socket.Options.SetRequestHeader(CallbackCorrelation.HeaderName, "stale-call");

        var exception = await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(
            ToWebSocketUri(new Uri(host.LocalBaseUri, host.Routes.MediaPath)),
            CancellationToken.None));

        Assert.NotEqual(WebSocketError.Success, exception.WebSocketErrorCode);
    }

    [Fact]
    public async Task MediaEndpoint_AcceptsAgreeingCorrelationHeaderAndQuery()
    {
        await using var host = new CallbackHost();
        var connected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await host.StartAsync();
        await using var registration = host.RegisterCall(
            "active-call",
            (_, _) => Task.CompletedTask,
            async (connection, cancellationToken) =>
            {
                connected.TrySetResult(connection.CallConnectionId);
                await connection.WebSocket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "test complete",
                    cancellationToken);
            });

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(CallbackCorrelation.AcsHeaderName, "active-call");
        await socket.ConnectAsync(
            ToWebSocketUri(WithCallId(host.LocalBaseUri, host.Routes.MediaPath, "active-call")),
            CancellationToken.None);

        Assert.Equal("active-call", await connected.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task EventEndpoint_RejectsDisagreeingCorrelationHeaders()
    {
        await using var host = new CallbackHost();
        await host.StartAsync();
        await using var registration = host.RegisterCall(
            "active-call",
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(host.LocalBaseUri, host.Routes.EventPath))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(CallbackCorrelation.AcsHeaderName, "active-call");
        request.Headers.Add(CallbackCorrelation.HeaderName, "stale-call");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EventEndpoint_RejectsRepeatedDisagreeingAcsHeaderValues()
    {
        await using var host = new CallbackHost();
        await host.StartAsync();
        await using var registration = host.RegisterCall(
            "active-call",
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(host.LocalBaseUri, host.Routes.EventPath))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(CallbackCorrelation.AcsHeaderName, new[] { "active-call", "stale-call" });

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EventEndpoint_RejectsAcsHeaderDisagreementWithThePayload()
    {
        await using var host = new CallbackHost();
        await host.StartAsync();
        await using var registration = host.RegisterCall(
            "active-call",
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(host.LocalBaseUri, host.Routes.EventPath))
        {
            Content = new StringContent(
                """{"data":{"callConnectionId":"stale-call"}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add(CallbackCorrelation.AcsHeaderName, "active-call");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RegistrationDisposal_WaitsForAnInFlightRequestWithoutDisposingItsGate()
    {
        await using var host = new CallbackHost();
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await host.StartAsync();
        var registration = host.RegisterCall(
            "active-call",
            async (_, _) =>
            {
                handlerEntered.TrySetResult();
                await releaseHandler.Task;
            },
            (_, _) => Task.CompletedTask);
        using var client = new HttpClient();

        var request = client.PostAsync(
            WithCallId(host.LocalBaseUri, host.Routes.EventPath, "active-call"),
            new StringContent("{}", Encoding.UTF8, "application/json"));
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposal = registration.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);

        releaseHandler.TrySetResult();
        Assert.Equal(HttpStatusCode.Accepted, (await request).StatusCode);
        await disposal;
    }

    [Fact]
    public async Task StopAsync_RejectsRegistrationAfterStoppingBegins()
    {
        await using var host = new CallbackHost();
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await host.StartAsync();
        await using var registration = host.RegisterCall(
            "active-call",
            async (_, _) =>
            {
                handlerEntered.TrySetResult();
                await releaseHandler.Task;
            },
            (_, _) => Task.CompletedTask);
        using var client = new HttpClient();

        var request = client.PostAsync(
            WithCallId(host.LocalBaseUri, host.Routes.EventPath, "active-call"),
            new StringContent("{}", Encoding.UTF8, "application/json"));
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stop = host.StopAsync();
        Assert.Throws<InvalidOperationException>(() => host.RegisterCall(
            "late-call",
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask));

        releaseHandler.TrySetResult();
        await stop;
        await request;
    }

    [Fact]
    public async Task StopAsync_UsesOneDeadlineWhenAHandlerIgnoresCancellation()
    {
        await using var host = new CallbackHost(new CallbackHostOptions
        {
            ShutdownTimeout = TimeSpan.FromMilliseconds(150),
        });
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await host.StartAsync();
        await using var registration = host.RegisterCall(
            "active-call",
            async (_, _) =>
            {
                handlerEntered.TrySetResult();
                await releaseHandler.Task;
            },
            (_, _) => Task.CompletedTask);
        using var client = new HttpClient();

        var request = client.PostAsync(
            WithCallId(host.LocalBaseUri, host.Routes.EventPath, "active-call"),
            new StringContent("{}", Encoding.UTF8, "application/json"));
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => host.StopAsync());
        stopwatch.Stop();

        Assert.Contains("deadline", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Shutdown took {stopwatch.Elapsed}.");

        releaseHandler.TrySetResult();
        try
        {
            await request;
        }
        catch (HttpRequestException)
        {
            // Kestrel can close the in-flight transport after the shutdown deadline.
        }
    }

    private static Uri WithCallId(Uri baseUri, string path, string callConnectionId)
    {
        return new UriBuilder(new Uri(baseUri, path))
        {
            Query = $"{CallbackCorrelation.QueryParameterName}={Uri.EscapeDataString(callConnectionId)}",
        }.Uri;
    }

    private static Uri ToWebSocketUri(Uri uri)
    {
        return new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeWs,
        }.Uri;
    }
}
