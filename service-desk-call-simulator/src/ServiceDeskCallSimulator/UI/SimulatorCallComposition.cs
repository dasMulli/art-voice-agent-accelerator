using ServiceDeskCallSimulator.Calls;
using ServiceDeskCallSimulator.Conversation;
using ServiceDeskCallSimulator.Monitoring;
using ServiceDeskCallSimulator.Presets;
using ServiceDeskCallSimulator.Speech;

namespace ServiceDeskCallSimulator.UI;

/// <summary>
/// The per-call owned resources composed by the WinForms boundary.
/// </summary>
internal sealed class SimulatorCallResources<TCallSession>(
    TCallSession callSession,
    ScriptedCallerOrchestrator orchestrator,
    IAudioMonitor monitor,
    CallerScriptSnapshot script)
    where TCallSession : IOwnedCallerCallSession
{
    public TCallSession CallSession { get; } = callSession;

    public ScriptedCallerOrchestrator Orchestrator { get; } = orchestrator;

    public IAudioMonitor Monitor { get; } = monitor;

    public CallerScriptSnapshot Script { get; } = script;
}

/// <summary>
/// Composes and tears down per-call resources without binding them to a specific form instance.
/// </summary>
internal static class SimulatorCallComposition
{
    private static readonly AudioMonitorFault MonitorStartupFault = new(
        "startup",
        "Local audio playback could not be started, so listen-along was disabled. The call continues.");

    public static async Task<SimulatorCallResources<TCallSession>> CreateAsync<TCallSession>(
        CallerScriptDraft draft,
        bool mutedLocally,
        Func<TCallSession> createCallSession,
        ISpeechPipelineFactory speechFactory,
        IAudioMonitorFactory monitorFactory,
        IGroundedReplyGenerator replyGenerator,
        Action<AudioMonitorFault>? onLocalMonitorFault = null)
        where TCallSession : IOwnedCallerCallSession
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(createCallSession);
        ArgumentNullException.ThrowIfNull(speechFactory);
        ArgumentNullException.ThrowIfNull(monitorFactory);
        ArgumentNullException.ThrowIfNull(replyGenerator);

        var snapshot = CallerScriptSnapshot.FromDraft(draft);
        var components = await PerCallResourceBuilder.CreateAsync(
            createCallSession,
            speechFactory.Create,
            () => CreateMonitor(monitorFactory, mutedLocally, onLocalMonitorFault),
            (call, speech, monitor) =>
            {
                monitor.IsMuted = mutedLocally;
                return new ScriptedCallerOrchestrator(snapshot, call, replyGenerator, speech, monitor);
            }).ConfigureAwait(false);

        return new SimulatorCallResources<TCallSession>(
            components.Call,
            components.Orchestrator,
            components.Monitor,
            snapshot);
    }

    private static IAudioMonitor CreateMonitor(
        IAudioMonitorFactory monitorFactory,
        bool mutedLocally,
        Action<AudioMonitorFault>? onLocalMonitorFault)
    {
        try
        {
            return new FaultIsolatingAudioMonitor(monitorFactory.Create())
            {
                IsMuted = mutedLocally,
            };
        }
        catch (Exception)
        {
            TryReportLocalMonitorFault(onLocalMonitorFault, MonitorStartupFault);
            return new NullAudioMonitor
            {
                IsMuted = mutedLocally,
            };
        }
    }

    private static void TryReportLocalMonitorFault(
        Action<AudioMonitorFault>? onLocalMonitorFault,
        AudioMonitorFault fault)
    {
        try
        {
            onLocalMonitorFault?.Invoke(fault);
        }
        catch
        {
            // Diagnostics must never affect call composition or ACS media startup.
        }
    }

    public static async Task DisposeAsync<TCallSession>(
        SimulatorCallResources<TCallSession> resources,
        Action<Exception>? onConversationCleanupFailure = null,
        Action<Exception>? onCallCleanupFailure = null)
        where TCallSession : IOwnedCallerCallSession
    {
        ArgumentNullException.ThrowIfNull(resources);

        try
        {
            await resources.Orchestrator.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            onConversationCleanupFailure?.Invoke(exception);
        }

        try
        {
            await resources.CallSession.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            onCallCleanupFailure?.Invoke(exception);
        }
    }
}
