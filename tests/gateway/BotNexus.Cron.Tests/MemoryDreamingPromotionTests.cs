using BotNexus.Cron.Actions;
using BotNexus.Memory;
using Moq;

namespace BotNexus.Cron.Tests;

public sealed class MemoryDreamingPromotionTests
{
    [Fact]
    public void BuildRoutingRules_NoWritableStores_ReturnsEmpty()
    {
        var registry = new Mock<ISharedMemoryStoreRegistry>();
        registry.Setup(r => r.GetWritableStores("agent-1")).Returns(new List<string>());

        var rules = MemoryDreamingCronAction.BuildRoutingRules(registry.Object, "agent-1");

        rules.ShouldBeEmpty();
    }

    [Fact]
    public void BuildRoutingRules_WithWritableStore_ReturnsCatchAllRule()
    {
        var registry = new Mock<ISharedMemoryStoreRegistry>();
        registry.Setup(r => r.GetWritableStores("agent-1")).Returns(new List<string> { "platform-knowledge" });

        var rules = MemoryDreamingCronAction.BuildRoutingRules(registry.Object, "agent-1");

        rules.Count.ShouldBe(1);
        rules[0].Category.ShouldBeNull(); // catch-all
        rules[0].MinConfidence.ShouldBe(0.7);
        rules[0].TargetStore.ShouldBe("platform-knowledge");
    }

    [Fact]
    public void BuildRoutingRules_MultipleWritableStores_UsesFirst()
    {
        var registry = new Mock<ISharedMemoryStoreRegistry>();
        registry.Setup(r => r.GetWritableStores("agent-1")).Returns(new List<string> { "store-a", "store-b" });

        var rules = MemoryDreamingCronAction.BuildRoutingRules(registry.Object, "agent-1");

        rules.Count.ShouldBe(1);
        rules[0].TargetStore.ShouldBe("store-a");
    }
}
