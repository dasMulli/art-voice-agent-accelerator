using ServiceDeskCallSimulator.Conversation;
using ServiceDeskCallSimulator.UI;

namespace ServiceDeskCallSimulator.Tests;

public sealed class TranscriptPresenterTests
{
    private static TranscriptTurn Turn(TranscriptSpeaker speaker, string text, TranscriptStatus status) =>
        new(DateTimeOffset.UtcNow, speaker, text, status);

    [Fact]
    public void Apply_InterimUpdate_ReplacesInPlaceInsteadOfAppending()
    {
        var presenter = new TranscriptPresenter();

        presenter.Apply(Turn(TranscriptSpeaker.ServiceDesk, "Hel", TranscriptStatus.Interim));
        presenter.Apply(Turn(TranscriptSpeaker.ServiceDesk, "Hello", TranscriptStatus.Interim));
        presenter.Apply(Turn(TranscriptSpeaker.ServiceDesk, "Hello there", TranscriptStatus.Interim));

        Assert.Single(presenter.Lines);
        Assert.Equal("Hello there", presenter.Lines[0].Text);
        Assert.True(presenter.Lines[0].IsInterim);
    }

    [Fact]
    public void Apply_FinalAfterInterim_CommitsOverThePendingLine()
    {
        var presenter = new TranscriptPresenter();
        presenter.Apply(Turn(TranscriptSpeaker.ServiceDesk, "Hel", TranscriptStatus.Interim));
        presenter.Apply(Turn(TranscriptSpeaker.ServiceDesk, "Hello, how can I help?", TranscriptStatus.Final));

        Assert.Single(presenter.Lines);
        Assert.Equal("Hello, how can I help?", presenter.Lines[0].Text);
        Assert.False(presenter.Lines[0].IsInterim);
    }

    [Fact]
    public void Apply_FinalTurnsAreRetainedAsSeparateLines()
    {
        var presenter = new TranscriptPresenter();
        presenter.Apply(Turn(TranscriptSpeaker.Caller, "Opening line.", TranscriptStatus.Final));
        presenter.Apply(Turn(TranscriptSpeaker.ServiceDesk, "How can I help?", TranscriptStatus.Final));
        presenter.Apply(Turn(TranscriptSpeaker.Caller, "My printer is broken.", TranscriptStatus.Final));

        Assert.Equal(3, presenter.Lines.Count);
        Assert.Equal(TranscriptSpeaker.Caller, presenter.Lines[0].Speaker);
        Assert.Equal(TranscriptSpeaker.ServiceDesk, presenter.Lines[1].Speaker);
        Assert.Equal(TranscriptSpeaker.Caller, presenter.Lines[2].Speaker);
        Assert.All(presenter.Lines, line => Assert.False(line.IsInterim));
    }

    [Fact]
    public void Apply_SameSpeakerRepeatedFinalTurnsDoNotCollapse()
    {
        // Once a speaker's interim placeholder is committed, later finals for that speaker
        // append new lines rather than overwriting the committed one.
        var presenter = new TranscriptPresenter();
        presenter.Apply(Turn(TranscriptSpeaker.ServiceDesk, "First question?", TranscriptStatus.Final));
        presenter.Apply(Turn(TranscriptSpeaker.ServiceDesk, "Second question?", TranscriptStatus.Final));

        Assert.Equal(2, presenter.Lines.Count);
        Assert.Equal("First question?", presenter.Lines[0].Text);
        Assert.Equal("Second question?", presenter.Lines[1].Text);
    }

    [Fact]
    public void Apply_InterimForOneSpeakerDoesNotAffectAnotherSpeakersPendingInterim()
    {
        var presenter = new TranscriptPresenter();
        presenter.Apply(Turn(TranscriptSpeaker.ServiceDesk, "Interim SD", TranscriptStatus.Interim));
        presenter.Apply(Turn(TranscriptSpeaker.Caller, "Final caller line.", TranscriptStatus.Final));

        Assert.Equal(2, presenter.Lines.Count);
        Assert.Equal(TranscriptSpeaker.ServiceDesk, presenter.Lines[0].Speaker);
        Assert.True(presenter.Lines[0].IsInterim);
        Assert.Equal(TranscriptSpeaker.Caller, presenter.Lines[1].Speaker);
    }

    [Fact]
    public void Apply_RaisesChangedWithReplacedFlagMatchingBehavior()
    {
        var presenter = new TranscriptPresenter();
        var changes = new List<TranscriptPresenterChange>();
        presenter.Changed += (_, change) => changes.Add(change);

        presenter.Apply(Turn(TranscriptSpeaker.ServiceDesk, "Hel", TranscriptStatus.Interim));
        presenter.Apply(Turn(TranscriptSpeaker.ServiceDesk, "Hello", TranscriptStatus.Interim));
        presenter.Apply(Turn(TranscriptSpeaker.Caller, "Hi.", TranscriptStatus.Final));

        Assert.Equal(3, changes.Count);
        Assert.False(changes[0].Replaced);
        Assert.True(changes[1].Replaced);
        Assert.Equal(0, changes[1].Index);
        Assert.False(changes[2].Replaced);
    }

    [Fact]
    public void Clear_RemovesAllLinesAndPendingInterimTracking()
    {
        var presenter = new TranscriptPresenter();
        var clearedRaised = 0;
        presenter.Cleared += (_, _) => clearedRaised++;

        presenter.Apply(Turn(TranscriptSpeaker.ServiceDesk, "Hel", TranscriptStatus.Interim));
        presenter.Clear();

        Assert.Empty(presenter.Lines);
        Assert.Equal(1, clearedRaised);

        // After clearing, a new interim for the same speaker starts a fresh line rather than
        // reusing stale bookkeeping.
        presenter.Apply(Turn(TranscriptSpeaker.ServiceDesk, "New", TranscriptStatus.Interim));
        Assert.Single(presenter.Lines);
        Assert.Equal("New", presenter.Lines[0].Text);
    }
}
