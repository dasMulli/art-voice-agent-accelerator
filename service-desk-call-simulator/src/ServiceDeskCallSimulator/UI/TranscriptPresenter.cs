using ServiceDeskCallSimulator.Conversation;

namespace ServiceDeskCallSimulator.UI;

/// <summary>
/// One rendered transcript line. Interim lines are replaced in place as later interim or
/// final updates arrive for the same speaker; final lines are retained permanently.
/// </summary>
public sealed record PresentedTranscriptLine(
    DateTimeOffset Timestamp,
    TranscriptSpeaker Speaker,
    string Text,
    bool IsInterim);

/// <summary>
/// Describes one mutation of the presented transcript so a UI can either append a new line
/// or replace an existing one in place without re-rendering the whole control.
/// </summary>
public sealed record TranscriptPresenterChange(int Index, bool Replaced);

/// <summary>
/// WinForms-independent transcript rendering rules: collapses a speaker's repeated interim
/// recognition updates into one updating line, and commits a final turn over any pending
/// interim line for that speaker. <see cref="ScriptedCallerOrchestrator"/> appends every
/// interim and final <see cref="TranscriptTurn"/> to its own transcript, so this type is
/// solely responsible for the "update rather than append endlessly" UI behavior.
/// </summary>
public sealed class TranscriptPresenter
{
    private readonly List<PresentedTranscriptLine> _lines = [];
    private readonly Dictionary<TranscriptSpeaker, int> _pendingInterimIndexBySpeaker = [];

    /// <summary>
    /// Gets the currently presented lines in display order.
    /// </summary>
    public IReadOnlyList<PresentedTranscriptLine> Lines => _lines;

    /// <summary>
    /// Raised whenever a line is appended or replaced.
    /// </summary>
    public event EventHandler<TranscriptPresenterChange>? Changed;

    /// <summary>
    /// Raised whenever <see cref="Clear"/> removes all presented lines, so a UI can clear its
    /// own display in lockstep rather than going out of sync with this presenter's state.
    /// </summary>
    public event EventHandler? Cleared;

    /// <summary>
    /// Applies one transcript turn, mutating the presented line for the turn's speaker when
    /// an interim placeholder is pending, or appending a new line otherwise.
    /// </summary>
    public void Apply(TranscriptTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        var line = new PresentedTranscriptLine(
            turn.Timestamp,
            turn.Speaker,
            turn.Text,
            IsInterim: turn.Status == TranscriptStatus.Interim);

        if (_pendingInterimIndexBySpeaker.TryGetValue(turn.Speaker, out var pendingIndex))
        {
            _lines[pendingIndex] = line;
            if (turn.Status == TranscriptStatus.Final)
            {
                _pendingInterimIndexBySpeaker.Remove(turn.Speaker);
            }

            Changed?.Invoke(this, new TranscriptPresenterChange(pendingIndex, Replaced: true));
            return;
        }

        _lines.Add(line);
        var index = _lines.Count - 1;
        if (turn.Status == TranscriptStatus.Interim)
        {
            _pendingInterimIndexBySpeaker[turn.Speaker] = index;
        }

        Changed?.Invoke(this, new TranscriptPresenterChange(index, Replaced: false));
    }

    /// <summary>
    /// Clears all presented lines. Used only when a new call's first transcript event
    /// arrives; completed calls retain their transcript.
    /// </summary>
    public void Clear()
    {
        _lines.Clear();
        _pendingInterimIndexBySpeaker.Clear();
        Cleared?.Invoke(this, EventArgs.Empty);
    }
}
