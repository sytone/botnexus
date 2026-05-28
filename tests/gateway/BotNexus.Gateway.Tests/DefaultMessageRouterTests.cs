using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Routing;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class DefaultMessageRouterTests
{
    [Fact]
    public async Task ResolveAsync_WithExplicitTarget_RoutesToTargetAgent()
    {
        var registry = CreateRegistryWithAgents("agent-a", "agent-b");
        var router = CreateRouter(registry, new InMemorySessionStore());

        var route = await router.ResolveAsync(CreateMessage(targetAgentId: "agent-b"));

        route.ShouldHaveSingleItem().ShouldBe("agent-b");
    }

    [Fact]
    public async Task ResolveAsync_WithSessionBoundAgent_RoutesToSessionAgent()
    {
        var registry = CreateRegistryWithAgents("agent-a", "agent-b");
        var sessions = new InMemorySessionStore();
        await sessions.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-b"));
        var router = CreateRouter(registry, sessions);

        var route = await router.ResolveAsync(CreateMessage(sessionId: "s1"));

        route.ShouldHaveSingleItem().ShouldBe("agent-b");
    }

    [Fact]
    public async Task ResolveAsync_WithoutExplicitOrSession_FallsBackToDefaultAgent()
    {
        var registry = CreateRegistryWithAgents("agent-a");
        var router = CreateRouter(registry, new InMemorySessionStore(), "agent-a");

        var route = await router.ResolveAsync(CreateMessage());

        route.ShouldHaveSingleItem().ShouldBe("agent-a");
    }

    [Fact]
    public async Task ResolveAsync_WhenNoAgentFound_ReturnsEmpty()
    {
        var router = CreateRouter(CreateRegistryWithAgents(), new InMemorySessionStore());

        var route = await router.ResolveAsync(CreateMessage());

        route.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_WithUnknownExplicitTarget_ReturnsEmpty()
    {
        var registry = CreateRegistryWithAgents("agent-a");
        var router = CreateRouter(registry, new InMemorySessionStore(), "agent-a");

        var route = await router.ResolveAsync(CreateMessage(targetAgentId: "missing"));

        route.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task ResolveAsync_WithEmptyOrWhitespaceTargetAgentId_FallsThroughToDefault_DoesNotThrow(string blank)
    {
        // Sub-PR 6.2 normalisation pin: the typed routing-hint lift treats null / empty /
        // whitespace as "no hint" rather than constructing a Vogen AgentId (which would
        // throw on whitespace). Pre-PR the router used IsNullOrEmpty + AgentId.From, so a
        // whitespace-only TargetAgentId would crash the route. Channel adapters historically
        // pass empty strings on "no override"; this fall-through is the intended shape.
        var registry = CreateRegistryWithAgents("agent-default");
        var router = CreateRouter(registry, new InMemorySessionStore(), "agent-default");

        var route = await router.ResolveAsync(CreateMessage(targetAgentId: blank));

        route.ShouldHaveSingleItem().ShouldBe("agent-default");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task ResolveAsync_WithEmptyOrWhitespaceSessionId_FallsThroughToDefault_DoesNotThrow(string blank)
    {
        // Sub-PR 6.2 normalisation pin: same shape as the TargetAgentId pin above, applied
        // to the session-binding priority level. Whitespace-only SessionId must not throw
        // and must not be looked up as a real session id; it falls through to the default
        // agent. Defends against a regression that reverts the lift to IsNullOrEmpty +
        // SessionId.From (which would throw on whitespace).
        var registry = CreateRegistryWithAgents("agent-default");
        var router = CreateRouter(registry, new InMemorySessionStore(), "agent-default");

        var route = await router.ResolveAsync(CreateMessage(sessionId: blank));

        route.ShouldHaveSingleItem().ShouldBe("agent-default");
    }

    private static DefaultAgentRegistry CreateRegistryWithAgents(params string[] agentIds)
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        foreach (var agentId in agentIds)
            registry.Register(CreateDescriptor(agentId));

        return registry;
    }

    private static DefaultMessageRouter CreateRouter(
        DefaultAgentRegistry registry,
        InMemorySessionStore sessions,
        string? defaultAgentId = null)
    {
        var options = new Mock<IOptionsMonitor<GatewayOptions>>();
        options.SetupGet(x => x.CurrentValue).Returns(new GatewayOptions { DefaultAgentId = defaultAgentId });
        return new DefaultMessageRouter(registry, sessions, NullLogger<DefaultMessageRouter>.Instance, options.Object);
    }

    private static AgentDescriptor CreateDescriptor(string agentId)
        => new()
        {
            AgentId = AgentId.From(agentId),
            DisplayName = agentId,
            ModelId = "test-model",
            ApiProvider = "test-provider"
        };

    private static InboundMessage CreateMessage(string? targetAgentId = null, string? sessionId = null)
        => new()
        {
            ChannelType = ChannelKey.From("web"),
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            ChannelAddress = ChannelAddress.From("conv-1"),
            Content = "hello",
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(targetAgentId, sessionId, null)
        };
}

