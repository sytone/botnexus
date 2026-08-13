using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BotNexus.Gateway.Conversations.Tests.Conversations;

/// <summary>
/// Pins the production path that mints an agent's default conversation (issue #2488).
/// </summary>
/// <remarks>
/// <para>
/// #196 removed <c>GetOrCreateDefaultAsync</c> on the premise that the default conversation was
/// "a routing fallback that no longer has callers". The premise was wrong: it was the only thing
/// that <em>created</em> the general per-agent conversation. With it gone, and every
/// <c>ConversationFactory</c> factory hard-coding <c>isDefault: false</c>, no agent created after
/// 2026-05-05 has ever had one - while the column, DTOs, portal ordering and cron retention
/// exemption all kept reading the flag.
/// </para>
/// <para>
/// Creation is <b>lazy</b>, not eager: the first conversation the router mints for an agent that
/// has no default becomes that agent's default. Lazy avoids an empty row for every agent that is
/// registered and never used, and it needs no hook into agent registration, so a default exists
/// exactly when there is something to show in it.
/// </para>
/// </remarks>
public sealed class DefaultConversationRouterDefaultConversationTests
{
    private static AgentId Agent(string id = "agent-2488") => AgentId.From(id);
    private static ChannelKey Channel(string type = "telegram") => ChannelKey.From(type);

    private static DefaultConversationRouter CreateRouter(IConversationStore store) =>
        new(store, new InMemorySessionStore(), NullLogger<DefaultConversationRouter>.Instance);

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveInbound_MintsDefaultConversation_ForAgentWithNone()
    {
        var store = new InMemoryConversationStore();
        var router = CreateRouter(store);
        var agentId = Agent();

        var result = await router.ResolveInboundAsync(agentId, Channel(), ChannelAddress.From("chat-1"));

        // This is the acceptance criterion: a PRODUCTION path yields IsDefault = true.
        // Reverting the router to always mint isDefault:false reddens this by name.
        result.Conversation.IsDefault.ShouldBeTrue();

        var persisted = await store.ListAsync(agentId);
        persisted.Count(c => c.IsDefault).ShouldBe(1);
    }

    [Fact]
    public async Task ResolveInbound_PersistsTheMintedDefault_SoAutoSelectCanFindIt()
    {
        var store = new InMemoryConversationStore();
        var router = CreateRouter(store);
        var agentId = Agent();

        var result = await router.ResolveInboundAsync(agentId, Channel(), ChannelAddress.From("chat-1"));

        // ClientStateStore auto-select does FirstOrDefault(c => c.IsDefault) over what the API
        // returns, so the flag must survive the round-trip through the store, not just live on
        // the in-flight object.
        var reloaded = await store.GetAsync(result.Conversation.ConversationId);
        reloaded.ShouldNotBeNull();
        reloaded.IsDefault.ShouldBeTrue();
    }

    // ── Uniqueness invariant ──────────────────────────────────────────────────

    [Fact]
    public async Task ResolveInbound_DoesNotMintASecondDefault_WhenAgentAlreadyHasOne()
    {
        var store = new InMemoryConversationStore();
        var router = CreateRouter(store);
        var agentId = Agent();

        var first = await router.ResolveInboundAsync(agentId, Channel(), ChannelAddress.From("chat-1"));
        var second = await router.ResolveInboundAsync(agentId, Channel(), ChannelAddress.From("chat-2"));

        first.Conversation.IsDefault.ShouldBeTrue();
        second.Conversation.IsDefault.ShouldBeFalse();
        second.Conversation.ConversationId.ShouldNotBe(first.Conversation.ConversationId);

        var persisted = await store.ListAsync(agentId);
        persisted.Count.ShouldBe(2);
        persisted.Count(c => c.IsDefault).ShouldBe(1);
    }

    [Fact]
    public async Task ResolveInbound_DoesNotMintADefault_WhenAgentsExistingDefaultIsArchived()
    {
        var store = new InMemoryConversationStore();
        var router = CreateRouter(store);
        var agentId = Agent();

        var first = await router.ResolveInboundAsync(agentId, Channel(), ChannelAddress.From("chat-1"));
        // Archive through the store's own archive path: SaveAsync refuses to persist an archived
        // conversation that still has an active session assigned, so hand-rolling the status flip
        // would test an invalid state rather than a real archived default.
        await store.ArchiveAsync(first.Conversation.ConversationId);

        var second = await router.ResolveInboundAsync(agentId, Channel(), ChannelAddress.From("chat-2"));

        // An archived default is still THE default - promoting a second one would create two
        // rows with is_default = 1 the moment the first is reopened.
        second.Conversation.IsDefault.ShouldBeFalse();
        (await store.ListAsync(agentId)).Count(c => c.IsDefault).ShouldBe(1);
    }

    [Fact]
    public async Task ResolveInbound_DefaultIsPerAgent_NotGlobal()
    {
        var store = new InMemoryConversationStore();
        var router = CreateRouter(store);

        var a = await router.ResolveInboundAsync(Agent("agent-a"), Channel(), ChannelAddress.From("chat-1"));
        var b = await router.ResolveInboundAsync(Agent("agent-b"), Channel(), ChannelAddress.From("chat-2"));

        // The uniqueness invariant is scoped per agent; a default for agent-a must not suppress
        // agent-b's own default.
        a.Conversation.IsDefault.ShouldBeTrue();
        b.Conversation.IsDefault.ShouldBeTrue();
        (await store.ListAsync(Agent("agent-a"))).Count(c => c.IsDefault).ShouldBe(1);
        (await store.ListAsync(Agent("agent-b"))).Count(c => c.IsDefault).ShouldBe(1);
    }

    // ── Sad paths: paths that must NOT promote a default ──────────────────────

    [Fact]
    public async Task ResolveInbound_DoesNotPromoteAnExistingConversationToDefault()
    {
        var store = new InMemoryConversationStore();
        var agentId = Agent();

        // A pre-existing non-default conversation with a live binding. Resolving onto it must
        // leave the flag alone: promotion-on-resolve would flip an arbitrary long-running
        // conversation into the agent's home the first time it is used.
        var existing = ConversationFactory.CreateForChannel(
            ConversationId.Create(), agentId, title: "existing");
        existing.ChannelBindings.Add(new ChannelBinding
        {
            ChannelType = Channel(),
            ChannelAddress = ChannelAddress.From("chat-1"),
            Mode = BindingMode.Interactive
        });
        await store.SaveAsync(existing);

        var router = CreateRouter(store);
        var result = await router.ResolveInboundAsync(agentId, Channel(), ChannelAddress.From("chat-1"));

        result.Conversation.ConversationId.ShouldBe(existing.ConversationId);
        result.Conversation.IsDefault.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveInbound_DoesNotPromoteToDefault_OnTheExplicitConversationIdPath()
    {
        var store = new InMemoryConversationStore();
        var agentId = Agent();

        var existing = ConversationFactory.CreateForChannel(
            ConversationId.Create(), agentId, title: "explicit");
        await store.SaveAsync(existing);

        var router = CreateRouter(store);
        var result = await router.ResolveInboundAsync(
            agentId, Channel(), ChannelAddress.From("chat-1"), existing.ConversationId);

        result.Conversation.ConversationId.ShouldBe(existing.ConversationId);
        result.Conversation.IsDefault.ShouldBeFalse();
        (await store.ListAsync(agentId)).Count(c => c.IsDefault).ShouldBe(0);
    }
}
