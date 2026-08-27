namespace ServiceDeskCallSimulator.Calls;

/// <summary>
/// Represents the lifecycle state of one outbound ACS call.
/// </summary>
public enum CallSessionState
{
    Idle,
    Dialing,
    Connected,
    Ending,
    Ended,
    Faulted,
}

/// <summary>
/// Describes one immutable outbound-call state transition.
/// </summary>
public sealed record CallStateChange(
    CallSessionState PreviousState,
    CallSessionState CurrentState,
    DateTimeOffset Timestamp,
    string Reason);

/// <summary>
/// Enforces the atomic state transitions allowed for an outbound call.
/// </summary>
public class CallSessionStateMachine
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private CallSessionState _state = CallSessionState.Idle;

    /// <summary>
    /// Initializes a state machine using the supplied clock for state notifications.
    /// </summary>
    public CallSessionStateMachine(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Raised synchronously with immutable details each time the state changes.
    /// </summary>
    public event EventHandler<CallStateChange>? StateChanged;

    /// <summary>
    /// Gets the current state.
    /// </summary>
    public CallSessionState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Returns whether a transition is permitted by the outbound call lifecycle.
    /// </summary>
    public static bool IsTransitionAllowed(CallSessionState from, CallSessionState to) =>
        (from, to) switch
        {
            (CallSessionState.Idle, CallSessionState.Dialing) => true,
            (CallSessionState.Dialing, CallSessionState.Connected) => true,
            (CallSessionState.Dialing, CallSessionState.Ending) => true,
            (CallSessionState.Dialing, CallSessionState.Ended) => true,
            (CallSessionState.Dialing, CallSessionState.Faulted) => true,
            (CallSessionState.Connected, CallSessionState.Ending) => true,
            (CallSessionState.Connected, CallSessionState.Ended) => true,
            (CallSessionState.Connected, CallSessionState.Faulted) => true,
            (CallSessionState.Ending, CallSessionState.Ended) => true,
            (CallSessionState.Ending, CallSessionState.Faulted) => true,
            (CallSessionState.Faulted, CallSessionState.Ending) => true,
            (CallSessionState.Faulted, CallSessionState.Ended) => true,
            _ => false,
        };

    /// <summary>
    /// Changes state or throws when the requested transition is not allowed.
    /// </summary>
    public CallStateChange TransitionTo(CallSessionState nextState, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_sync)
        {
            if (!IsTransitionAllowed(_state, nextState))
            {
                throw new InvalidOperationException($"The transition from {_state} to {nextState} is not allowed.");
            }

            var change = new CallStateChange(_state, nextState, _timeProvider.GetUtcNow(), reason);
            _state = nextState;
            StateChanged?.Invoke(this, change);
            return change;
        }
    }

    /// <summary>
    /// Changes state when permitted, returning false when the state has already advanced concurrently.
    /// </summary>
    public virtual bool TryTransitionTo(CallSessionState nextState, string reason, out CallStateChange? change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_sync)
        {
            if (_state == nextState || !IsTransitionAllowed(_state, nextState))
            {
                change = null;
                return false;
            }

            change = new CallStateChange(_state, nextState, _timeProvider.GetUtcNow(), reason);
            _state = nextState;
            StateChanged?.Invoke(this, change);
            return true;
        }
    }
}
