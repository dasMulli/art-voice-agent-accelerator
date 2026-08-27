namespace ServiceDeskCallSimulator.Callback;

/// <summary>
/// Owns one active call registration and removes it from the callback host when disposed.
/// </summary>
public sealed class CallRegistration : IAsyncDisposable
{
    private readonly Func<Task> _removeAsync;
    private int _disposed;

    internal CallRegistration(string callConnectionId, Func<Task> removeAsync)
    {
        CallConnectionId = callConnectionId;
        _removeAsync = removeAsync;
    }

    /// <summary>
    /// Gets the ACS call connection ID accepted by this registration.
    /// </summary>
    public string CallConnectionId { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _removeAsync().ConfigureAwait(false);
        }
    }
}
