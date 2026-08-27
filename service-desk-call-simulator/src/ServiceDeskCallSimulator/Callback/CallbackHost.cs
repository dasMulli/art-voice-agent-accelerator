using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ServiceDeskCallSimulator.Callback;

/// <summary>
/// Hosts local event and media callback endpoints for the lifetime of one simulator session.
/// </summary>
public sealed class CallbackHost : IAsyncDisposable
{
    private readonly CallbackHostOptions _options;
    private readonly Dictionary<string, ActiveCallRegistration> _registrations = new(StringComparer.Ordinal);
    private readonly object _registrationSync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private WebApplication? _application;
    private int _boundPort;
    private bool _stoppingStarted;
    private bool _hasStopped;
    private bool _disposed;

    /// <summary>
    /// Initializes a new callback host with a random route token.
    /// </summary>
    public CallbackHost(CallbackHostOptions? options = null, CallbackRoute? routes = null)
    {
        _options = options ?? new CallbackHostOptions();
        ValidateOptions(_options);
        Routes = routes ?? new CallbackRoute();
    }

    /// <summary>
    /// Gets the randomized event and media routes for this process.
    /// </summary>
    public CallbackRoute Routes { get; }

    /// <summary>
    /// Gets the actual loopback port after <see cref="StartAsync"/> completes.
    /// </summary>
    public int BoundPort => _boundPort;

    /// <summary>
    /// Gets the loopback base URI after <see cref="StartAsync"/> completes.
    /// </summary>
    public Uri LocalBaseUri => BoundPort > 0
        ? new Uri($"http://127.0.0.1:{BoundPort}/", UriKind.Absolute)
        : throw new InvalidOperationException("The callback host has not started.");

    /// <summary>
    /// Starts Kestrel on loopback without blocking the UI thread.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_hasStopped)
            {
                throw new InvalidOperationException("A stopped callback host cannot be restarted. Create a new host for a new session.");
            }

            if (_application is not null)
            {
                return;
            }

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, _options.Port);
            });

            var application = builder.Build();
            application.UseWebSockets();
            application.MapPost(Routes.EventPath, HandleEventAsync);
            application.Map(Routes.MediaPath, HandleMediaAsync);

            try
            {
                await application.StartAsync(cancellationToken).ConfigureAwait(false);
                _boundPort = GetBoundPort(application);
                _application = application;
            }
            catch
            {
                await application.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Registers handlers for one active ACS call connection ID.
    /// </summary>
    public CallRegistration RegisterCall(
        string callConnectionId,
        CallbackEventHandler eventHandler,
        MediaConnectionHandler mediaHandler)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(callConnectionId);
        ArgumentNullException.ThrowIfNull(eventHandler);
        ArgumentNullException.ThrowIfNull(mediaHandler);

        lock (_registrationSync)
        {
            if (_stoppingStarted)
            {
                throw new InvalidOperationException("The callback host is stopping and cannot accept new call registrations.");
            }

            if (_registrations.ContainsKey(callConnectionId))
            {
                throw new InvalidOperationException($"A callback registration already exists for call '{callConnectionId}'.");
            }

            var registration = new ActiveCallRegistration(eventHandler, mediaHandler);
            _registrations.Add(callConnectionId, registration);
            return new CallRegistration(
                callConnectionId,
                () => RemoveRegistrationAsync(callConnectionId, registration));
        }
    }

    /// <summary>
    /// Stops Kestrel using a bounded timeout and removes all active call registrations.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ActiveCallRegistration[] registrations;
        lock (_registrationSync)
        {
            _stoppingStarted = true;
            _hasStopped = true;
            registrations = _registrations.Values.ToArray();
            _registrations.Clear();
        }

        _stopping.Cancel();

        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        shutdown.CancelAfter(_options.ShutdownTimeout);
        var lifecycleGateAcquired = false;
        try
        {
            try
            {
                await _lifecycleGate.WaitAsync(shutdown.Token).ConfigureAwait(false);
                lifecycleGateAcquired = true;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Callback host shutdown exceeded its deadline while waiting for the lifecycle transition.",
                    exception);
            }

            try
            {
                var quiescence = Task.WhenAll(
                    registrations.Select(registration => registration.WaitForQuiescenceAsync(shutdown.Token)));
                var application = _application;
                var kestrelStop = application is null
                    ? Task.CompletedTask
                    : application.StopAsync(shutdown.Token);

                await Task.WhenAll(quiescence, kestrelStop).WaitAsync(shutdown.Token).ConfigureAwait(false);

                if (application is not null)
                {
                    await application.DisposeAsync().AsTask().WaitAsync(shutdown.Token).ConfigureAwait(false);
                    _application = null;
                    _boundPort = 0;
                }
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Callback host shutdown exceeded its deadline while quiescing callbacks or stopping Kestrel.",
                    exception);
            }
        }
        finally
        {
            if (lifecycleGateAcquired)
            {
                _lifecycleGate.Release();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _stopping.Dispose();
            _lifecycleGate.Dispose();
        }
    }

    private async Task HandleEventAsync(HttpContext context)
    {
        var resolution = await ResolveEventAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
        if (resolution.StatusCode is not null)
        {
            context.Response.StatusCode = resolution.StatusCode.Value;
            return;
        }

        if (!TryGetRegistration(resolution.CallConnectionId!, out var registration))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await registration.Gate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            if (registration.Removed || !TryGetRegistration(resolution.CallConnectionId!, out var current)
                || !ReferenceEquals(current, registration))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted,
                _stopping.Token);
            await registration.EventHandler(
                new CallbackEvent(resolution.CallConnectionId!, resolution.Body!, context.Request.ContentType),
                linkedCancellation.Token).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status202Accepted;
        }
        finally
        {
            registration.Gate.Release();
        }
    }

    private async Task HandleMediaAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var correlation = ResolveRequestCorrelation(context.Request);
        if (correlation.IsAmbiguous
            || correlation.CallConnectionId is null
            || !TryGetRegistration(correlation.CallConnectionId, out var registration))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await registration.Gate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            if (registration.Removed || !TryGetRegistration(correlation.CallConnectionId, out var current)
                || !ReferenceEquals(current, registration))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            registration.AcquireMediaLease();
        }
        finally
        {
            registration.Gate.Release();
        }

        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted,
                _stopping.Token);
            await registration.MediaHandler(
                new MediaConnection(correlation.CallConnectionId, socket),
                linkedCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            registration.ReleaseMediaLease();
        }
    }

    private async Task<EventResolution> ResolveEventAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > _options.MaximumEventBodyBytes)
        {
            return EventResolution.WithStatus(StatusCodes.Status413PayloadTooLarge);
        }

        var body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return EventResolution.WithStatus(StatusCodes.Status413PayloadTooLarge);
        }

        var requestCorrelation = ResolveRequestCorrelation(request);
        if (requestCorrelation.IsAmbiguous)
        {
            return EventResolution.WithStatus(StatusCodes.Status404NotFound);
        }

        var payloadCorrelation = TryResolvePayloadCorrelation(body);
        if (payloadCorrelation.IsAmbiguous
            || requestCorrelation.CallConnectionId is not null
                && payloadCorrelation.CallConnectionId is not null
                && !string.Equals(
                    requestCorrelation.CallConnectionId,
                    payloadCorrelation.CallConnectionId,
                    StringComparison.Ordinal))
        {
            return EventResolution.WithStatus(StatusCodes.Status404NotFound);
        }

        var callConnectionId = requestCorrelation.CallConnectionId ?? payloadCorrelation.CallConnectionId;
        return callConnectionId is null
            ? EventResolution.WithStatus(StatusCodes.Status404NotFound)
            : EventResolution.Success(callConnectionId, body);
    }

    private async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        var readBuffer = new byte[Math.Min(_options.MaximumEventBodyBytes + 1, 8192)];

        while (true)
        {
            var read = await request.Body.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > _options.MaximumEventBodyBytes)
            {
                return null;
            }

            await buffer.WriteAsync(readBuffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static CorrelationResolution ResolveRequestCorrelation(HttpRequest request)
    {
        var values = new List<string>();
        AddValues(values, request.Query[CallbackCorrelation.QueryParameterName]);
        foreach (var headerName in CallbackCorrelation.HeaderNames)
        {
            // IHeaderDictionary lookups are case-insensitive, so the ACS casing of
            // "x-ms-call-connection-id" matches regardless of how the peer sends it.
            AddValues(values, request.Headers[headerName]);
        }

        var distinctValues = values.Distinct(StringComparer.Ordinal).ToArray();
        return distinctValues.Length switch
        {
            0 => new CorrelationResolution(null, false),
            1 => new CorrelationResolution(distinctValues[0], false),
            _ => new CorrelationResolution(null, true),
        };
    }

    private static void AddValues(List<string> target, IEnumerable<string?> values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                target.Add(value);
            }
        }
    }

    private static CorrelationResolution TryResolvePayloadCorrelation(byte[] body)
    {
        if (body.Length == 0)
        {
            return new CorrelationResolution(null, false);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var callConnectionIds = new HashSet<string>(StringComparer.Ordinal);
            FindCallConnectionIds(document.RootElement, callConnectionIds);
            return callConnectionIds.Count switch
            {
                0 => new CorrelationResolution(null, false),
                1 => new CorrelationResolution(callConnectionIds.Single(), false),
                _ => new CorrelationResolution(null, true),
            };
        }
        catch (JsonException)
        {
            return new CorrelationResolution(null, false);
        }
    }

    private static void FindCallConnectionIds(JsonElement element, ISet<string> callConnectionIds)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "callConnectionId", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    {
                        callConnectionIds.Add(property.Value.GetString()!);
                    }
                    else
                    {
                        FindCallConnectionIds(property.Value, callConnectionIds);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    FindCallConnectionIds(item, callConnectionIds);
                }

                break;
        }
    }

    private async Task RemoveRegistrationAsync(string callConnectionId, ActiveCallRegistration registration)
    {
        lock (_registrationSync)
        {
            if (_registrations.TryGetValue(callConnectionId, out var current)
                && ReferenceEquals(current, registration))
            {
                _registrations.Remove(callConnectionId);
            }
        }

        await registration.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            registration.Removed = true;
        }
        finally
        {
            registration.Gate.Release();
        }

        await registration.WaitForMediaQuiescenceAsync().ConfigureAwait(false);
    }

    private bool TryGetRegistration(string callConnectionId, out ActiveCallRegistration registration)
    {
        lock (_registrationSync)
        {
            return _registrations.TryGetValue(callConnectionId, out registration!);
        }
    }

    private static int GetBoundPort(WebApplication application)
    {
        var addresses = application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not expose its bound address.");
        var address = addresses.SingleOrDefault(candidate =>
            Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && IPAddress.TryParse(uri.Host, out var ipAddress)
            && IPAddress.IsLoopback(ipAddress))
            ?? throw new InvalidOperationException("Kestrel did not bind a loopback address.");

        return new Uri(address, UriKind.Absolute).Port;
    }

    private static void ValidateOptions(CallbackHostOptions options)
    {
        if (options.Port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The callback port must be between 0 and 65535.");
        }

        if (options.MaximumEventBodyBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The event body limit must be positive.");
        }

        if (options.ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The shutdown timeout must be positive.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class ActiveCallRegistration
    {
        private readonly object _mediaSync = new();
        private TaskCompletionSource? _mediaQuiescence;
        private int _activeMediaHandlers;

        public ActiveCallRegistration(CallbackEventHandler eventHandler, MediaConnectionHandler mediaHandler)
        {
            EventHandler = eventHandler;
            MediaHandler = mediaHandler;
        }

        public CallbackEventHandler EventHandler { get; }

        public MediaConnectionHandler MediaHandler { get; }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool Removed { get; set; }

        public void AcquireMediaLease()
        {
            lock (_mediaSync)
            {
                _activeMediaHandlers++;
                _mediaQuiescence ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void ReleaseMediaLease()
        {
            TaskCompletionSource? quiescence = null;
            lock (_mediaSync)
            {
                _activeMediaHandlers--;
                if (_activeMediaHandlers == 0)
                {
                    quiescence = _mediaQuiescence;
                }
            }

            quiescence?.TrySetResult();
        }

        public Task WaitForMediaQuiescenceAsync()
        {
            lock (_mediaSync)
            {
                return _activeMediaHandlers == 0
                    ? Task.CompletedTask
                    : _mediaQuiescence!.Task;
            }
        }

        public async Task WaitForQuiescenceAsync(CancellationToken cancellationToken)
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            Gate.Release();
            await WaitForMediaQuiescenceAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record EventResolution(string? CallConnectionId, byte[]? Body, int? StatusCode)
    {
        public static EventResolution Success(string callConnectionId, byte[] body) => new(callConnectionId, body, null);

        public static EventResolution WithStatus(int statusCode) => new(null, null, statusCode);
    }

    private sealed record CorrelationResolution(string? CallConnectionId, bool IsAmbiguous);
}
