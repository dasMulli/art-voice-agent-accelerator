namespace ServiceDeskCallSimulator.Conversation;

/// <summary>
/// Identifies the source of a transcript item.
/// </summary>
public enum TranscriptSpeaker
{
    Caller,
    ServiceDesk,
    System,
}

/// <summary>
/// Identifies whether a recognition item is provisional or complete.
/// </summary>
public enum TranscriptStatus
{
    Interim,
    Final,
}

/// <summary>
/// An immutable, timestamped item in a caller conversation transcript.
/// </summary>
public sealed record TranscriptTurn(
    DateTimeOffset Timestamp,
    TranscriptSpeaker Speaker,
    string Text,
    TranscriptStatus Status);

/// <summary>
/// Describes the caller conversation's current externally observable activity.
/// </summary>
public enum CallerActivityState
{
    Idle,
    Listening,
    Thinking,
    Speaking,
    Ending,
    Ended,
    Faulted,
}

/// <summary>
/// An immutable caller activity transition for UI presentation.
/// </summary>
public sealed record CallerActivityChange(
    CallerActivityState PreviousState,
    CallerActivityState CurrentState,
    DateTimeOffset Timestamp,
    string Reason);

/// <summary>
/// The action selected by the grounded caller model.
/// </summary>
public enum GroundedReplyAction
{
    Reply,
    HangUp,
}

/// <summary>
/// A validated, structured response from the grounded caller model.
/// </summary>
public sealed record GroundedModelDecision(
    GroundedReplyAction Action,
    string? SpokenText,
    string Reason);

/// <summary>
/// Indicates invalid, empty, or refused caller-model output.
/// </summary>
public sealed class GroundedReplyException : InvalidOperationException
{
    /// <summary>
    /// Initializes an exception describing a safe model-output failure category.
    /// </summary>
    public GroundedReplyException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes an exception with its safe underlying parsing error.
    /// </summary>
    public GroundedReplyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
