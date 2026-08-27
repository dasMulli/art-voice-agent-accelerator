using ServiceDeskCallSimulator.Monitoring;

namespace ServiceDeskCallSimulator.Tests;

public sealed class AudioMonitorSourceGateTests
{
    [Fact]
    public void InboundMonitoring_IsAllowedOnlyForAudibleFramesOutsideCallerPlayback()
    {
        var gate = new AudioMonitorSourceGate();

        Assert.True(gate.ShouldMonitorInbound(isSilent: false));
        Assert.False(gate.ShouldMonitorInbound(isSilent: true));
        Assert.False(gate.IsOutboundActive);

        gate.BeginOutbound();

        Assert.True(gate.IsOutboundActive);
        Assert.False(gate.ShouldMonitorInbound(isSilent: false));
        Assert.False(gate.ShouldMonitorInbound(isSilent: true));

        gate.EndOutbound();

        Assert.False(gate.IsOutboundActive);
        Assert.True(gate.ShouldMonitorInbound(isSilent: false));
    }

    [Fact]
    public void NestedOutboundPlaybacks_ReleaseTheMonitorOnlyAfterTheLastOneEnds()
    {
        var gate = new AudioMonitorSourceGate();

        gate.BeginOutbound();
        gate.BeginOutbound();
        gate.EndOutbound();

        Assert.True(gate.IsOutboundActive);
        Assert.False(gate.ShouldMonitorInbound(isSilent: false));

        gate.EndOutbound();

        Assert.True(gate.ShouldMonitorInbound(isSilent: false));
    }

    [Fact]
    public void UnbalancedEnd_ThrowsAndLeavesTheGateInboundEnabled()
    {
        var gate = new AudioMonitorSourceGate();

        Assert.Throws<InvalidOperationException>(gate.EndOutbound);

        Assert.False(gate.IsOutboundActive);
        Assert.True(gate.ShouldMonitorInbound(isSilent: false));
    }
}
