using ServiceDeskCallSimulator.UI;

namespace ServiceDeskCallSimulator.Tests;

public sealed class CallGenerationGateTests
{
    [Fact]
    public void Advance_ProducesIncreasingGenerationsStartingAboveZero()
    {
        var gate = new CallGenerationGate();
        var first = gate.Advance();
        var second = gate.Advance();

        Assert.True(first > 0);
        Assert.True(second > first);
    }

    [Fact]
    public void IsCurrent_TrueOnlyForTheMostRecentlyAdvancedGeneration()
    {
        var gate = new CallGenerationGate();
        var first = gate.Advance();
        Assert.True(gate.IsCurrent(first));

        var second = gate.Advance();
        Assert.False(gate.IsCurrent(first));
        Assert.True(gate.IsCurrent(second));
    }

    [Fact]
    public void Retire_MakesTheGenerationNoLongerCurrent()
    {
        var gate = new CallGenerationGate();
        var generation = gate.Advance();
        gate.Retire(generation);

        Assert.False(gate.IsCurrent(generation));
        Assert.Equal(0, gate.Current);
    }

    [Fact]
    public void Retire_DoesNotAffectANewerGeneration()
    {
        var gate = new CallGenerationGate();
        var first = gate.Advance();
        var second = gate.Advance();

        gate.Retire(first);

        Assert.True(gate.IsCurrent(second));
    }

    [Fact]
    public void LateEventCarryingARetiredGeneration_IsIgnoredByCallers()
    {
        var gate = new CallGenerationGate();
        var generation = gate.Advance();
        var applied = 0;

        void HandleWorkerEvent(long eventGeneration)
        {
            if (gate.IsCurrent(eventGeneration))
            {
                applied++;
            }
        }

        HandleWorkerEvent(generation); // in-flight event for the active call
        gate.Retire(generation); // per-call teardown completes
        HandleWorkerEvent(generation); // a late event races the teardown

        Assert.Equal(1, applied);
    }
}
