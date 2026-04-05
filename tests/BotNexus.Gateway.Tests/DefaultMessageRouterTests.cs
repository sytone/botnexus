using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Routing;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
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

        route.Should().Equal("agent-b");
    }

    [Fact]
    public async Task ResolveAsync_WithSessionBoundAgent_RoutesToSessionAgent()
    {
        var registry = CreateRegistryWithAgents("agent-a", "agent-b");
        var sessions = new InMemorySessionStore();
        await sessions.GetOrCreateAsync("s1", "agent-b");
        var router = CreateRouter(registry, sessions);

        var route = await router.ResolveAsync(CreateMessage(sessionId: "s1"));

        route.Should().Equal("agent-b");
    }

    [Fact]
    public async Task ResolveAsync_WithoutExplicitOrSession_FallsBackToDefaultAgent()
    {
        var registry = CreateRegistryWithAgents("agent-a");
        var router = CreateRouter(registry, new InMemorySessionStore(), "agent-a");

        var route = await router.ResolveAsync(CreateMessage());

        route.Should().Equal("agent-a");
    }

    [Fact]
    public async Task ResolveAsync_WhenNoAgentFound_ReturnsEmpty()
    {
        var router = CreateRouter(CreateRegistryWithAgents(), new InMemorySessionStore());

        var route = await router.ResolveAsync(CreateMessage());

        route.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_WithUnknownExplicitTarget_ReturnsEmpty()
    {
        var registry = CreateRegistryWithAgents("agent-a");
        var router = CreateRouter(registry, new InMemorySessionStore(), "agent-a");

        var route = await router.ResolveAsync(CreateMessage(targetAgentId: "missing"));

        route.Should().BeEmpty();
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
            AgentId = agentId,
            DisplayName = agentId,
            ModelId = "test-model",
            ApiProvider = "test-provider"
        };

    private static InboundMessage CreateMessage(string? targetAgentId = null, string? sessionId = null)
        => new()
        {
            ChannelType = "web",
            SenderId = "sender-1",
            ConversationId = "conv-1",
            Content = "hello",
            TargetAgentId = targetAgentId,
            SessionId = sessionId
        };
}
