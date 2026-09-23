using System.IO.Abstractions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Providers;
using BotNexus.Memory.Embeddings;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Providers;

public sealed class MemoryTemporalDecayCompositionTests : IAsyncLifetime
{
    private string _root = string.Empty;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "botnexus-decay-composition", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Factory_UsesCurrentEffectiveAgentPolicyForCachedStore()
    {
        var agentId = AgentId.From("agent");
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(Descriptor(agentId, enabled: true, halfLifeDays: 9));
        await using var factory = new EmbeddingAwareMemoryStoreFactory(
            _ => Path.Combine(_root, "memory.db"),
            MemoryEmbeddingService.Disabled,
            new FileSystem(),
            agentRegistry: registry);
        var store = factory.Create(agentId);

        var first = await store.SearchWithReportAsync("query");
        first.TemporalDecay?.Enabled.ShouldBeTrue();
        first.TemporalDecay?.HalfLifeDays.ShouldBe(9d);

        registry.Update(agentId, Descriptor(agentId, enabled: false, halfLifeDays: 21)).ShouldBeTrue();
        factory.Create(agentId).ShouldBeSameAs(store);

        var updated = await store.SearchWithReportAsync("query");
        updated.TemporalDecay?.Enabled.ShouldBeFalse();
        updated.TemporalDecay?.HalfLifeDays.ShouldBe(21d);
    }

    private static AgentDescriptor Descriptor(AgentId agentId, bool enabled, int halfLifeDays) => new()
    {
        AgentId = agentId,
        DisplayName = "Agent",
        ModelId = "test-model",
        ApiProvider = "test-provider",
        Memory = new MemoryAgentConfig
        {
            Enabled = true,
            Search = new MemorySearchAgentConfig
            {
                TemporalDecay = new TemporalDecayAgentConfig
                {
                    Enabled = enabled,
                    HalfLifeDays = halfLifeDays
                }
            }
        }
    };
}
