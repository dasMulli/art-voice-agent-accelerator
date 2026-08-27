using ServiceDeskCallSimulator.UI;

namespace ServiceDeskCallSimulator.Tests;

public sealed class PerCallResourceBuilderTests
{
    [Theory]
    [InlineData("speech", new[] { "call" })]
    [InlineData("monitor", new[] { "speech", "call" })]
    [InlineData("orchestrator", new[] { "monitor", "speech", "call" })]
    public async Task CreateAsync_FailedPerCallConstructionDisposesEveryAcquiredResourceInReverseOrder(
        string failingStage,
        string[] expectedDisposalOrder)
    {
        var disposalOrder = new List<string>();
        var call = new DisposableResource("call", disposalOrder);
        var speech = new DisposableResource("speech", disposalOrder);
        var monitor = new DisposableResource("monitor", disposalOrder);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PerCallResourceBuilder.CreateAsync(
                () => call,
                () => failingStage == "speech"
                    ? throw new InvalidOperationException("speech creation failed")
                    : speech,
                () => failingStage == "monitor"
                    ? throw new InvalidOperationException("monitor creation failed")
                    : monitor,
                (_, _, _) => failingStage == "orchestrator"
                    ? throw new InvalidOperationException("orchestrator creation failed")
                    : new DisposableResource("orchestrator", disposalOrder)));

        Assert.Equal(expectedDisposalOrder, disposalOrder);
    }

    [Fact]
    public async Task CreateAsync_ContinuesRollbackWhenAnEarlierResourceDisposalFails()
    {
        var disposalOrder = new List<string>();
        var call = new DisposableResource("call", disposalOrder);
        var speech = new DisposableResource("speech", disposalOrder, throwOnDispose: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PerCallResourceBuilder.CreateAsync<
                DisposableResource,
                DisposableResource,
                DisposableResource,
                DisposableResource>(
                () => call,
                () => speech,
                () => throw new InvalidOperationException("monitor creation failed"),
                (_, _, _) => throw new InvalidOperationException("not reached")));

        Assert.Equal(["speech", "call"], disposalOrder);
    }

    private sealed class DisposableResource(
        string name,
        List<string> disposalOrder,
        bool throwOnDispose = false) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            disposalOrder.Add(name);
            if (throwOnDispose)
            {
                throw new InvalidOperationException($"{name} cleanup failed");
            }

            return ValueTask.CompletedTask;
        }
    }
}
