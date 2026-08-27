namespace ServiceDeskCallSimulator.UI;

/// <summary>
/// Acquires per-call resources transactionally. A failed later acquisition releases each
/// earlier resource in reverse ownership order before the dial attempt returns to Ready.
/// </summary>
internal static class PerCallResourceBuilder
{
    public static async Task<(TCall Call, TSpeech Speech, TMonitor Monitor, TOrchestrator Orchestrator)>
        CreateAsync<TCall, TSpeech, TMonitor, TOrchestrator>(
            Func<TCall> createCall,
            Func<TSpeech> createSpeech,
            Func<TMonitor> createMonitor,
            Func<TCall, TSpeech, TMonitor, TOrchestrator> createOrchestrator)
        where TCall : IAsyncDisposable
        where TSpeech : IAsyncDisposable
        where TMonitor : IAsyncDisposable
        where TOrchestrator : IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(createCall);
        ArgumentNullException.ThrowIfNull(createSpeech);
        ArgumentNullException.ThrowIfNull(createMonitor);
        ArgumentNullException.ThrowIfNull(createOrchestrator);

        var acquired = new List<IAsyncDisposable>(capacity: 4);
        try
        {
            var call = createCall();
            acquired.Add(call);

            var speech = createSpeech();
            acquired.Add(speech);

            var monitor = createMonitor();
            acquired.Add(monitor);

            var orchestrator = createOrchestrator(call, speech, monitor);
            acquired.Add(orchestrator);

            return (call, speech, monitor, orchestrator);
        }
        catch
        {
            await DisposeReverseIgnoringFailuresAsync(acquired).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DisposeReverseIgnoringFailuresAsync(IReadOnlyList<IAsyncDisposable> acquired)
    {
        for (var index = acquired.Count - 1; index >= 0; index--)
        {
            try
            {
                await acquired[index].DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Continue so every resource acquired by this failed call attempt is released.
            }
        }
    }
}
