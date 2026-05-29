using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Phase 9 / P9-B-1 (#615): idempotency + provenance contract for
/// <see cref="LegacyConversationResolver"/>. The resolver is the single source of
/// truth for the per-agent <c>legacy:{agentId}</c> conversation; all three session
/// stores route their backfill paths through it.
/// </summary>
public sealed class LegacyConversationResolverTests
{
    [Fact]
    public async Task ResolveAsync_WhenNoLegacyExists_CreatesWithCanonicalTitle()
    {
        var store = new InMemoryConversationStore();
        var resolver = new LegacyConversationResolver(store);
        var agentId = AgentId.From("agent-alpha");

        var created = await resolver.ResolveAsync(agentId);

        created.Title.ShouldBe("legacy:agent-alpha");
        created.AgentId.ShouldBe(agentId);
    }

    [Fact]
    public async Task ResolveAsync_StampsInitiator_AsAgentCitizen()
    {
        // G-1 maintainer decision (2026-05-28): "default to a Legacy conversation for
        // the agent the session aligns to as the initiator." The agent is the initiator
        // for legacy ungrouped sessions because no real human citizen can be inferred
        // from the orphan row.
        var store = new InMemoryConversationStore();
        var resolver = new LegacyConversationResolver(store);
        var agentId = AgentId.From("agent-beta");

        var created = await resolver.ResolveAsync(agentId);

        created.Initiator.ShouldNotBeNull();
        created.Initiator!.Value.Kind.ShouldBe(CitizenKind.Agent);
        created.Initiator.Value.AsAgent.ShouldBe(agentId);
    }

    [Fact]
    public async Task ResolveAsync_LegacyConversationIsNotMarkedDefault()
    {
        // The legacy conversation must not shadow the agent's real default conversation
        // — it is a backfill container, not a primary thread.
        var store = new InMemoryConversationStore();
        var resolver = new LegacyConversationResolver(store);

        var created = await resolver.ResolveAsync(AgentId.From("agent-gamma"));

        created.IsDefault.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_WhenLegacyExists_ReturnsExisting_NoDuplicate()
    {
        var store = new InMemoryConversationStore();
        var resolver = new LegacyConversationResolver(store);
        var agentId = AgentId.From("agent-delta");

        var first = await resolver.ResolveAsync(agentId);
        var second = await resolver.ResolveAsync(agentId);

        second.ConversationId.ShouldBe(first.ConversationId);

        var all = await store.ListAsync(agentId);
        all.Count(c => c.Title == "legacy:agent-delta").ShouldBe(1);
    }

    [Fact]
    public async Task ResolveAsync_DifferentAgents_GetDistinctLegacyConversations()
    {
        var store = new InMemoryConversationStore();
        var resolver = new LegacyConversationResolver(store);

        var first = await resolver.ResolveAsync(AgentId.From("agent-one"));
        var second = await resolver.ResolveAsync(AgentId.From("agent-two"));

        first.ConversationId.ShouldNotBe(second.ConversationId);
        first.Title.ShouldBe("legacy:agent-one");
        second.Title.ShouldBe("legacy:agent-two");
    }

    [Fact]
    public async Task ResolveAsync_ConcurrentCalls_SameAgent_OnlyCreatesOnce()
    {
        // Pins the per-agent SemaphoreSlim path: 20 concurrent callers must observe a
        // single legacy conversation, not 20. The fast-path no-lock check is racy by
        // design; the slow-path double-check is what enforces uniqueness.
        var store = new InMemoryConversationStore();
        var resolver = new LegacyConversationResolver(store);
        var agentId = AgentId.From("agent-concurrent");

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => resolver.ResolveAsync(agentId)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        results.Select(r => r.ConversationId).Distinct().Count().ShouldBe(1);
        var stored = await store.ListAsync(agentId);
        stored.Count(c => c.Title == $"legacy:{agentId.Value}").ShouldBe(1);
    }

    [Fact]
    public void LegacyTitleFor_ReturnsExpectedFormat()
    {
        LegacyConversationResolver.LegacyTitleFor(AgentId.From("alex")).ShouldBe("legacy:alex");
        LegacyConversationResolver.LegacyTitleFor(AgentId.From("agent-with-hyphens")).ShouldBe("legacy:agent-with-hyphens");
    }

    [Fact]
    public void Ctor_RejectsNullConversationStore()
    {
        Should.Throw<ArgumentNullException>(() =>
            new LegacyConversationResolver(null!, NullLogger<LegacyConversationResolver>.Instance));
    }
}
