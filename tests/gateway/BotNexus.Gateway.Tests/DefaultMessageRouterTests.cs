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
            TargetAgentId = targetAgentId,
            SessionId = sessionId
        };
}

