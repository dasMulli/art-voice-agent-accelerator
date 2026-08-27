using ServiceDeskCallSimulator.Presets;

namespace ServiceDeskCallSimulator.Conversation;

/// <summary>
/// Produces one validated, script-grounded caller decision for a completed service-desk turn.
/// </summary>
public interface IGroundedReplyGenerator
{
    /// <summary>
    /// Generates the next caller decision using only the immutable script and the completed
    /// (<see cref="TranscriptStatus.Final"/>) conversation turns. Interim recognition fragments
    /// must not be supplied.
    /// </summary>
    Task<GroundedModelDecision> GenerateAsync(
        CallerScriptSnapshot script,
        IReadOnlyList<TranscriptTurn> transcript,
        CancellationToken cancellationToken);
}
