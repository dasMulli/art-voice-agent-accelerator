using Azure;
using Azure.Communication.CallAutomation;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using ServiceDeskCallSimulator.Callback;
using ServiceDeskCallSimulator.Azure;
using ServiceDeskCallSimulator.Calls;
using ServiceDeskCallSimulator.Configuration;

namespace ServiceDeskCallSimulator.Tests;

public sealed class AcsCallSessionTests
{
    [Fact]
    public void ServiceRegistration_UsesOneSharedEntraCredentialForCallAutomation()
    {
        var services = new ServiceCollection();
        services.AddServiceDeskCallSimulatorCore(
            new SimulatorSettings
            {
                Acs = new AcsSettings
                {
                    Endpoint = "https://example.communication.azure.com",
                },
            });
        using var provider = services.BuildServiceProvider();

        var credential = provider.GetRequiredService<global::Azure.Core.TokenCredential>();
        Assert.Same(credential, provider.GetRequiredService<global::Azure.Core.TokenCredential>());
        Assert.IsType<ChainedTokenCredential>(credential);
        Assert.IsType<CallAutomationClient>(provider.GetRequiredService<CallAutomationClient>());
        Assert.IsType<AcsCallAutomationGateway>(provider.GetRequiredService<ICallAutomationGateway>());
    }

    [Fact]
    public void Gateway_UsesEndpointCredentialAndExactCreateCallMediaOptions()
    {
        var endpoint = new Uri("https://example.communication.azure.com");
        var gateway = new AcsCallAutomationGateway(
            endpoint,
            AzureCredentialFactory.CreateLocalDeveloperCredential());
        var request = new AcsCreateCallRequest(
            "+43800223359",
            "+33801150311",
            new Uri("https://public.example/events"),
            new AcsMediaStreamingRequest(
                new Uri("wss://public.example/media"),
                StartMediaStreaming: true,
                EnableBidirectional: true,
                EnableDtmfTones: false));

        var options = AcsCallAutomationGateway.CreateSdkOptions(request);

        Assert.Equal(endpoint, gateway.Endpoint);
        Assert.Equal(
            "+33801150311",
            ((global::Azure.Communication.PhoneNumberIdentifier)options.CallInvite.Target).PhoneNumber);
        Assert.Equal("+43800223359", options.CallInvite.SourceCallerIdNumber!.PhoneNumber);
        Assert.Equal(request.CallbackUri, options.CallbackUri);
        Assert.Equal(MediaStreamingAudioChannel.Unmixed, options.MediaStreamingOptions.MediaStreamingAudioChannel);
        Assert.Equal(StreamingTransport.Websocket, options.MediaStreamingOptions.MediaStreamingTransport);
        Assert.Equal(request.MediaStreaming.TransportUri, options.MediaStreamingOptions.TransportUri);
        Assert.Equal(MediaStreamingContent.Audio, options.MediaStreamingOptions.MediaStreamingContent);
        Assert.True(options.MediaStreamingOptions.StartMediaStreaming);
        Assert.True(options.MediaStreamingOptions.EnableBidirectional);
        Assert.False(options.MediaStreamingOptions.EnableDtmfTones);
        Assert.Equal(AudioFormat.Pcm16KMono, options.MediaStreamingOptions.AudioFormat);
    }

    [Theory]
    [InlineData("123", "+33801150311")]
    [InlineData("+43800223359", "not-a-number")]
    public async Task StartAsync_InvalidPhoneNumber_DoesNotCallAzure(string source, string destination)
    {
        var gateway = new FakeGateway();
        await using var session = CreateSession(gateway, new FakeCallbackHost());

        await Assert.ThrowsAsync<ArgumentException>(() => session.StartAsync(source, destination));

        Assert.Equal(0, gateway.CreateCalls);
    }

    [Fact]
    public void StateMachine_AllowsOnlyDocumentedTransitions()
    {
        var allowed = new HashSet<(CallSessionState From, CallSessionState To)>
        {
            (CallSessionState.Idle, CallSessionState.Dialing),
            (CallSessionState.Dialing, CallSessionState.Connected),
            (CallSessionState.Dialing, CallSessionState.Ending),
            (CallSessionState.Dialing, CallSessionState.Ended),
            (CallSessionState.Dialing, CallSessionState.Faulted),
            (CallSessionState.Connected, CallSessionState.Ending),
            (CallSessionState.Connected, CallSessionState.Ended),
            (CallSessionState.Connected, CallSessionState.Faulted),
            (CallSessionState.Ending, CallSessionState.Ended),
            (CallSessionState.Ending, CallSessionState.Faulted),
            (CallSessionState.Faulted, CallSessionState.Ending),
            (CallSessionState.Faulted, CallSessionState.Ended),
        };

        foreach (var from in Enum.GetValues<CallSessionState>())
        {
            foreach (var to in Enum.GetValues<CallSessionState>())
            {
                Assert.Equal(allowed.Contains((from, to)), CallSessionStateMachine.IsTransitionAllowed(from, to));
            }
        }

        var stateMachine = new CallSessionStateMachine();
        var notifications = new List<CallStateChange>();
        stateMachine.StateChanged += (_, change) => notifications.Add(change);
        stateMachine.TransitionTo(CallSessionState.Dialing, "test dialing");

        Assert.Throws<InvalidOperationException>(() => stateMachine.TransitionTo(CallSessionState.Idle, "invalid"));
        Assert.Single(notifications);
        Assert.Equal(CallSessionState.Idle, notifications[0].PreviousState);
        Assert.Equal(CallSessionState.Dialing, notifications[0].CurrentState);
        Assert.True(notifications[0].Timestamp <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task CreateEventProcessor_ClosesRegistrationRaceAndTransitionsConnected()
    {
        var gateway = new FakeGateway();
        var callbackHost = new FakeCallbackHost
        {
            RequireInitialWaitBeforeRegistration = true,
            IsInitialWaitRequested = () => gateway.InitialWaitRequested,
        };
        await using var session = CreateSession(gateway, callbackHost);

        await session.StartAsync("+43800223359", "+33801150311");
        gateway.InitialEvent.TrySetResult(new AcsCreateCallEvent(true, null));

        await EventuallyAsync(() => session.State == CallSessionState.Connected);

        Assert.True(gateway.InitialWaitRequested);
        Assert.True(callbackHost.Registered);
        Assert.Equal("+43800223359", gateway.LastRequest!.SourcePhoneNumber);
        Assert.Equal("+33801150311", gateway.LastRequest.DestinationPhoneNumber);
        Assert.Equal(callbackHost.PublicEventUri, gateway.LastRequest.CallbackUri);
        Assert.Equal(callbackHost.PublicMediaUri, gateway.LastRequest.MediaStreaming.TransportUri);
        Assert.True(gateway.LastRequest.MediaStreaming.StartMediaStreaming);
        Assert.True(gateway.LastRequest.MediaStreaming.EnableBidirectional);
        Assert.False(gateway.LastRequest.MediaStreaming.EnableDtmfTones);
    }

    [Fact]
    public async Task CreateCallFailure_TransitionsFaultedAndCleansRegistration()
    {
        var gateway = new FakeGateway();
        var callbackHost = new FakeCallbackHost();
        await using var session = CreateSession(gateway, callbackHost);

        await session.StartAsync("+43800223359", "+33801150311");
        gateway.InitialEvent.TrySetResult(new AcsCreateCallEvent(false, "create failed"));

        await EventuallyAsync(() => session.State == CallSessionState.Faulted && callbackHost.RegistrationDisposed);
        Assert.Equal(0, gateway.Connection.HangUpCalls);
    }

    [Fact]
    public async Task DialTimeout_FaultsAndHangsUpCreatedCall()
    {
        var gateway = new FakeGateway();
        await using var session = CreateSession(
            gateway,
            new FakeCallbackHost(),
            new AcsCallSessionOptions
            {
                DialTimeout = TimeSpan.FromMilliseconds(50),
                CleanupTimeout = TimeSpan.FromSeconds(1),
            });

        await session.StartAsync("+43800223359", "+33801150311");

        await EventuallyAsync(() => session.State == CallSessionState.Faulted && gateway.Connection.HangUpCalls == 1);
        Assert.Equal(1, gateway.Connection.HangUpCalls);
    }

    [Fact]
    public async Task DialTimeout_CoversCreateAndConnectedWaitUsingOneDeadline()
    {
        var timeProvider = new ManualTimeProvider();
        var gateway = new FakeGateway
        {
            BlockCreate = true,
            IgnoreCreateCancellation = true,
        };
        await using var session = CreateSession(
            gateway,
            new FakeCallbackHost(),
            new AcsCallSessionOptions
            {
                DialTimeout = TimeSpan.FromMilliseconds(100),
                CleanupTimeout = TimeSpan.FromSeconds(1),
            },
            timeProvider);

        var startup = session.StartAsync("+43800223359", "+33801150311");
        await gateway.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        timeProvider.Advance(TimeSpan.FromMilliseconds(90));
        gateway.ReleaseCreate();
        await startup;

        timeProvider.Advance(TimeSpan.FromMilliseconds(9));
        await Task.Yield();
        Assert.Equal(CallSessionState.Dialing, session.State);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await EventuallyAsync(() => session.State == CallSessionState.Faulted && gateway.Connection.HangUpCalls == 1);
    }

    [Fact]
    public async Task CallbackBatch_ParsesCallAndMediaEvents()
    {
        var gateway = new FakeGateway();
        var callbackHost = new FakeCallbackHost();
        await using var session = CreateSession(gateway, callbackHost);
        var mediaChanges = new List<AcsMediaStateChange>();
        session.MediaStateChanged += (_, change) => mediaChanges.Add(change);
        await session.StartAsync("+43800223359", "+33801150311");

        await callbackHost.DispatchAsync(
            """
            [
              {"id":"1","source":"/acs","specversion":"1.0","type":"Microsoft.Communication.CallConnected","data":{"callConnectionId":"call-1"}},
              {"id":"2","source":"/acs","specversion":"1.0","type":"Microsoft.Communication.MediaStreamingStarted","data":{"callConnectionId":"call-1"}}
            ]
            """);

        await EventuallyAsync(() => session.State == CallSessionState.Connected);
        Assert.Equal(AcsMediaSessionState.Started, session.MediaState);
        Assert.Single(mediaChanges);
    }

    [Fact]
    public async Task RemoteDisconnect_CancelsAndEndsWithoutSendingHangUp()
    {
        var gateway = new FakeGateway();
        var callbackHost = new FakeCallbackHost();
        await using var session = CreateSession(gateway, callbackHost);
        await session.StartAsync("+43800223359", "+33801150311");

        await callbackHost.DispatchAsync(Event("Microsoft.Communication.CallDisconnected"));

        await EventuallyAsync(() => session.State == CallSessionState.Ended && callbackHost.RegistrationDisposed);
        Assert.Equal(0, gateway.Connection.HangUpCalls);
    }

    [Fact]
    public async Task MediaFailure_FaultsTheCallAndMediaState()
    {
        var gateway = new FakeGateway();
        var callbackHost = new FakeCallbackHost();
        await using var session = CreateSession(gateway, callbackHost);
        await session.StartAsync("+43800223359", "+33801150311");

        await callbackHost.DispatchAsync(Event("Microsoft.Communication.MediaStreamingFailed"));

        await EventuallyAsync(() => session.State == CallSessionState.Faulted && callbackHost.RegistrationDisposed);
        Assert.Equal(AcsMediaSessionState.Failed, session.MediaState);
    }

    [Fact]
    public async Task MediaStopped_UpdatesMediaStateWithoutEndingCall()
    {
        var gateway = new FakeGateway();
        var callbackHost = new FakeCallbackHost();
        await using var session = CreateSession(gateway, callbackHost);
        await session.StartAsync("+43800223359", "+33801150311");

        await callbackHost.DispatchAsync(Event("Microsoft.Communication.CallConnected"));
        await callbackHost.DispatchAsync(Event("Microsoft.Communication.MediaStreamingStopped"));

        await EventuallyAsync(() => session.State == CallSessionState.Connected);
        Assert.Equal(AcsMediaSessionState.Stopped, session.MediaState);
    }

    [Fact]
    public async Task CreateCallFailedCallback_TransitionsFaulted()
    {
        var gateway = new FakeGateway();
        var callbackHost = new FakeCallbackHost();
        await using var session = CreateSession(gateway, callbackHost);
        await session.StartAsync("+43800223359", "+33801150311");

        await callbackHost.DispatchAsync(Event("Microsoft.Communication.CreateCallFailed"));

        await EventuallyAsync(() => session.State == CallSessionState.Faulted && callbackHost.RegistrationDisposed);
    }

    [Fact]
    public async Task RepeatedHangUp_IssuesAtMostOneSdkRequest()
    {
        var gateway = new FakeGateway();
        await using var session = CreateSession(gateway, new FakeCallbackHost());
        await session.StartAsync("+43800223359", "+33801150311");

        await Task.WhenAll(session.HangUpAsync(), session.HangUpAsync(), session.HangUpAsync());

        Assert.Equal(1, gateway.Connection.HangUpCalls);
        Assert.True(gateway.Connection.LastForEveryone);
        Assert.Equal(CallSessionState.Ended, session.State);
    }

    [Fact]
    public async Task DisposeDuringDelayedCreate_HangsUpReturnedCallWithoutRegisteringIt()
    {
        var gateway = new FakeGateway
        {
            BlockCreate = true,
            IgnoreCreateCancellation = true,
        };
        var callbackHost = new FakeCallbackHost();
        var session = CreateSession(
            gateway,
            callbackHost,
            new AcsCallSessionOptions
            {
                DialTimeout = TimeSpan.FromSeconds(1),
                CleanupTimeout = TimeSpan.FromSeconds(1),
            });

        var startup = session.StartAsync("+43800223359", "+33801150311");
        await gateway.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disposal = session.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        gateway.ReleaseCreate();

        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        Assert.Equal(1, gateway.Connection.HangUpCalls);
        Assert.False(callbackHost.Registered);
        Assert.False(callbackHost.RegistrationDisposed);
    }

    [Fact]
    public async Task ConcurrentHangUps_AwaitOneFailingRequestBeforeCleanup()
    {
        var gateway = new FakeGateway
        {
            Connection =
            {
                BlockHangUp = true,
                HangUpFailure = new RequestFailedException("ACS hang-up failed."),
            },
        };
        var callbackHost = new FakeCallbackHost();
        await using var session = CreateSession(gateway, callbackHost);
        await session.StartAsync("+43800223359", "+33801150311");

        var firstHangUp = session.HangUpAsync();
        await gateway.Connection.HangUpStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondHangUp = session.HangUpAsync();

        Assert.False(secondHangUp.IsCompleted);
        Assert.Equal(CallSessionState.Ending, session.State);
        Assert.False(callbackHost.RegistrationDisposed);

        gateway.Connection.ReleaseHangUp();

        await Assert.ThrowsAsync<RequestFailedException>(() => firstHangUp);
        await Assert.ThrowsAsync<RequestFailedException>(() => secondHangUp);
        Assert.Equal(1, gateway.Connection.HangUpCalls);
        await EventuallyAsync(() => callbackHost.RegistrationDisposed);
        Assert.Equal(CallSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task HangUpFailureAfterRemoteDisconnect_IsTerminalSuccess()
    {
        var gateway = new FakeGateway
        {
            Connection =
            {
                BlockHangUp = true,
                HangUpFailure = new RequestFailedException("Call was already disconnected."),
            },
        };
        var callbackHost = new FakeCallbackHost();
        await using var session = CreateSession(gateway, callbackHost);
        await session.StartAsync("+43800223359", "+33801150311");

        var hangUp = session.HangUpAsync();
        await gateway.Connection.HangUpStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await callbackHost.DispatchAsync(Event("Microsoft.Communication.CallDisconnected"));
        gateway.Connection.ReleaseHangUp();

        await hangUp;

        Assert.Equal(1, gateway.Connection.HangUpCalls);
        Assert.Equal(CallSessionState.Ended, session.State);
    }

    [Fact]
    public async Task HangUpFailureWhenDisconnectWinsFaultTransition_IsTerminalSuccess()
    {
        var gateway = new FakeGateway
        {
            Connection =
            {
                BlockHangUp = true,
                HangUpFailure = new RequestFailedException("Call was already disconnected."),
            },
        };
        var callbackHost = new FakeCallbackHost();
        var stateMachine = new FaultTransitionRaceStateMachine();
        await using var session = new AcsCallSession(callbackHost, gateway, stateMachine);
        await session.StartAsync("+43800223359", "+33801150311");

        stateMachine.BlockNextFaultTransition();
        var hangUp = session.HangUpAsync();
        await gateway.Connection.HangUpStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        gateway.Connection.ReleaseHangUp();
        await stateMachine.FaultTransitionAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await callbackHost.DispatchAsync(Event("Microsoft.Communication.CallDisconnected"));
        stateMachine.ReleaseFaultTransition();
        await hangUp;

        Assert.Equal(1, gateway.Connection.HangUpCalls);
        Assert.Equal(CallSessionState.Ended, session.State);
    }

    [Fact]
    public async Task HangUpWithoutCallerCancellation_UsesCleanupDeadline()
    {
        var gateway = new FakeGateway { Connection = { BlockHangUp = true } };
        var session = CreateSession(
            gateway,
            new FakeCallbackHost(),
            new AcsCallSessionOptions
            {
                DialTimeout = TimeSpan.FromSeconds(1),
                CleanupTimeout = TimeSpan.FromMilliseconds(50),
            });
        await session.StartAsync("+43800223359", "+33801150311");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.HangUpAsync());

        Assert.Equal(1, gateway.Connection.HangUpCalls);
        Assert.Equal(CallSessionState.Faulted, session.State);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task HangUpAndRemoteDisconnectRace_NeverHangsUpMoreThanOnce()
    {
        var gateway = new FakeGateway { Connection = { BlockHangUp = true } };
        var callbackHost = new FakeCallbackHost();
        await using var session = CreateSession(gateway, callbackHost);
        await session.StartAsync("+43800223359", "+33801150311");

        var manualHangUp = session.HangUpAsync();
        await gateway.Connection.HangUpStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await callbackHost.DispatchAsync(Event("Microsoft.Communication.CallDisconnected"));
        gateway.Connection.ReleaseHangUp();
        await manualHangUp;

        Assert.Equal(1, gateway.Connection.HangUpCalls);
        Assert.Equal(CallSessionState.Ended, session.State);
    }

    private static AcsCallSession CreateSession(
        FakeGateway gateway,
        FakeCallbackHost callbackHost,
        AcsCallSessionOptions? options = null,
        TimeProvider? timeProvider = null) =>
        new(callbackHost, gateway, options: options, timeProvider: timeProvider);

    private static string Event(string type) =>
        System.Text.Json.JsonSerializer.Serialize(
            new
            {
                id = "1",
                source = "/acs",
                specversion = "1.0",
                type,
                data = new { callConnectionId = "call-1" },
            });

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        await Task.Run(async () =>
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            while (!condition())
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException("The expected asynchronous condition was not reached.");
                }

                await Task.Delay(10);
            }
        });
    }

    private sealed class FakeGateway : ICallAutomationGateway
    {
        private readonly TaskCompletionSource _createRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeCallConnection Connection { get; } = new();

        public int CreateCalls { get; private set; }

        public AcsCreateCallRequest? LastRequest { get; private set; }

        public bool BlockCreate { get; init; }

        public bool IgnoreCreateCancellation { get; init; }

        public TaskCompletionSource CreateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<AcsCreateCallEvent> InitialEvent { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool InitialWaitRequested { get; private set; }

        public async Task<AcsCallCreation> CreateCallAsync(
            AcsCreateCallRequest request,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            LastRequest = request;
            if (BlockCreate)
            {
                CreateStarted.TrySetResult();
                if (IgnoreCreateCancellation)
                {
                    await _createRelease.Task.ConfigureAwait(false);
                }
                else
                {
                    await _createRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return new AcsCallCreation(
                "call-1",
                async eventCancellationToken =>
                {
                    InitialWaitRequested = true;
                    return await InitialEvent.Task.WaitAsync(eventCancellationToken);
                });
        }

        public ICallConnectionHandle GetCallConnection(string callConnectionId)
        {
            Assert.Equal("call-1", callConnectionId);
            return Connection;
        }

        public void ReleaseCreate() => _createRelease.TrySetResult();
    }

    private sealed class FakeCallConnection : ICallConnectionHandle
    {
        private readonly TaskCompletionSource _hangUpRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource HangUpStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BlockHangUp { get; set; }

        public RequestFailedException? HangUpFailure { get; set; }

        public int HangUpCalls { get; private set; }

        public bool LastForEveryone { get; private set; }

        public async Task HangUpAsync(bool forEveryone, CancellationToken cancellationToken)
        {
            HangUpCalls++;
            LastForEveryone = forEveryone;
            HangUpStarted.TrySetResult();
            if (BlockHangUp)
            {
                await _hangUpRelease.Task.WaitAsync(cancellationToken);
            }

            if (HangUpFailure is not null)
            {
                throw HangUpFailure;
            }
        }

        public void ReleaseHangUp() => _hangUpRelease.TrySetResult();
    }

    private sealed class FakeCallbackHost : ICallCallbackRegistrationHost
    {
        private CallbackEventHandler? _eventHandler;
        private MediaConnectionHandler? _mediaHandler;

        public Uri PublicEventUri { get; } = new("https://public.example/events");

        public Uri PublicMediaUri { get; } = new("wss://public.example/media");

        public bool Registered { get; private set; }

        public bool RegistrationDisposed { get; private set; }

        public bool RequireInitialWaitBeforeRegistration { get; init; }

        public Func<bool>? IsInitialWaitRequested { get; init; }

        public IAsyncDisposable RegisterCall(
            string callConnectionId,
            CallbackEventHandler eventHandler,
            MediaConnectionHandler mediaHandler)
        {
            if (RequireInitialWaitBeforeRegistration)
            {
                Assert.NotNull(IsInitialWaitRequested);
                Assert.True(IsInitialWaitRequested!());
            }

            Assert.Equal("call-1", callConnectionId);
            _eventHandler = eventHandler;
            _mediaHandler = mediaHandler;
            Registered = true;
            return new AsyncRegistration(() => RegistrationDisposed = true);
        }

        public Task DispatchAsync(string payload)
        {
            Assert.NotNull(_eventHandler);
            return _eventHandler!(
                new CallbackEvent("call-1", System.Text.Encoding.UTF8.GetBytes(payload), "application/cloudevents+json"),
                CancellationToken.None);
        }

        private sealed class AsyncRegistration : IAsyncDisposable
        {
            private readonly Action _onDispose;

            public AsyncRegistration(Action onDispose)
            {
                _onDispose = onDispose;
            }

            public ValueTask DisposeAsync()
            {
                _onDispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FaultTransitionRaceStateMachine : CallSessionStateMachine
    {
        private int _blockFaultTransition;

        public TaskCompletionSource FaultTransitionAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowFaultTransition { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void BlockNextFaultTransition() => Volatile.Write(ref _blockFaultTransition, 1);

        public void ReleaseFaultTransition() => AllowFaultTransition.TrySetResult();

        public override bool TryTransitionTo(
            CallSessionState nextState,
            string reason,
            out CallStateChange? change)
        {
            if (nextState == CallSessionState.Faulted
                && Interlocked.Exchange(ref _blockFaultTransition, 0) == 1)
            {
                FaultTransitionAttempted.TrySetResult();
                AllowFaultTransition.Task.GetAwaiter().GetResult();
            }

            return base.TryTransitionTo(nextState, reason, out change);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
        private long _timestamp;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _utcNow;
            }
        }

        public override long GetTimestamp()
        {
            lock (_sync)
            {
                return _timestamp;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_sync)
            {
                _timers.Add(timer);
                ConfigureTimer(timer, dueTime, period);
            }

            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsed));
            }

            long targetTimestamp;
            lock (_sync)
            {
                targetTimestamp = checked(_timestamp + elapsed.Ticks);
            }

            while (true)
            {
                ManualTimer? nextTimer;
                lock (_sync)
                {
                    nextTimer = _timers
                        .Where(timer => timer.DueTimestamp is long dueTimestamp && dueTimestamp <= targetTimestamp)
                        .OrderBy(timer => timer.DueTimestamp)
                        .FirstOrDefault();
                    if (nextTimer is null)
                    {
                        AdvanceClock(targetTimestamp);
                        return;
                    }

                    AdvanceClock(nextTimer.DueTimestamp!.Value);
                    nextTimer.RescheduleAfterTick();
                }

                nextTimer.Invoke();
            }
        }

        private bool ChangeTimer(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                if (!_timers.Contains(timer))
                {
                    return false;
                }

                ConfigureTimer(timer, dueTime, period);
                return true;
            }
        }

        private void DisposeTimer(ManualTimer timer)
        {
            lock (_sync)
            {
                _timers.Remove(timer);
                timer.ClearSchedule();
            }
        }

        private void ConfigureTimer(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            ValidateTimerValue(dueTime, nameof(dueTime));
            ValidateTimerValue(period, nameof(period));
            timer.Schedule(
                dueTime == Timeout.InfiniteTimeSpan ? null : checked(_timestamp + dueTime.Ticks),
                period <= TimeSpan.Zero ? null : period.Ticks);
        }

        private void AdvanceClock(long timestamp)
        {
            var elapsedTicks = timestamp - _timestamp;
            _timestamp = timestamp;
            _utcNow += TimeSpan.FromTicks(elapsedTicks);
        }

        private static void ValidateTimerValue(TimeSpan value, string parameterName)
        {
            if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _provider;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private long? _periodTicks;

            public ManualTimer(ManualTimeProvider provider, TimerCallback callback, object? state)
            {
                _provider = provider;
                _callback = callback;
                _state = state;
            }

            public long? DueTimestamp { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                _provider.ChangeTimer(this, dueTime, period);

            public void Dispose() => _provider.DisposeTimer(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Schedule(long? dueTimestamp, long? periodTicks)
            {
                DueTimestamp = dueTimestamp;
                _periodTicks = periodTicks;
            }

            public void ClearSchedule()
            {
                DueTimestamp = null;
                _periodTicks = null;
            }

            public void RescheduleAfterTick()
            {
                DueTimestamp = _periodTicks is long periodTicks
                    ? checked(DueTimestamp!.Value + periodTicks)
                    : null;
            }

            public void Invoke() => _callback(_state);
        }
    }
}
