namespace ServiceDeskCallSimulator.UI;

/// <summary>
/// Dispatches worker notifications onto one captured UI synchronization context in FIFO order.
/// </summary>
public interface IUiEventDispatcher
{
    /// <summary>
    /// Queues an action for serialized execution on the UI context.
    /// </summary>
    void Post(Action action);
}

/// <summary>
/// Serializes event work posted to a captured WinForms synchronization context.
/// </summary>
public sealed class SerializedUiEventDispatcher : IUiEventDispatcher
{
    private readonly SynchronizationContext _synchronizationContext;
    private readonly object _sync = new();
    private readonly Queue<Action> _pending = [];
    private bool _drainScheduled;

    /// <summary>
    /// Initializes a dispatcher for the specified UI synchronization context.
    /// </summary>
    public SerializedUiEventDispatcher(SynchronizationContext synchronizationContext)
    {
        _synchronizationContext = synchronizationContext
            ?? throw new ArgumentNullException(nameof(synchronizationContext));
    }

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var scheduleDrain = false;
        lock (_sync)
        {
            _pending.Enqueue(action);
            if (!_drainScheduled)
            {
                _drainScheduled = true;
                scheduleDrain = true;
            }
        }

        if (scheduleDrain)
        {
            _synchronizationContext.Post(static state => ((SerializedUiEventDispatcher)state!).Drain(), this);
        }
    }

    private void Drain()
    {
        while (true)
        {
            Action? action;
            lock (_sync)
            {
                if (_pending.Count == 0)
                {
                    _drainScheduled = false;
                    return;
                }

                action = _pending.Dequeue();
            }

            action();
        }
    }
}

internal sealed class InlineUiEventDispatcher : IUiEventDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}
