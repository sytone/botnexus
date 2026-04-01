using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using BotNexus.Gateway;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Tests.Integration.Tests;

public class AgentRouterTests
{
    [Fact]
    public void ResolveTargets_RoutesToNamedAgent_WhenMessageSpecifiesAgent()
    {
        var router = CreateRouter(
            runners: [new FakeRunner("default"), new FakeRunner("planner"), new FakeRunner("writer")],
            cfg =>
            {
                cfg.Gateway.DefaultAgent = "default";
                cfg.Agents.Named["planner"] = new AgentConfig();
                cfg.Agents.Named["writer"] = new AgentConfig();
            });

        var message = MessageWithMetadata(("agent", "writer"));
        var targets = router.ResolveTargets(message);

        targets.Should().ContainSingle();
        targets[0].AgentName.Should().Be("writer");
    }

    [Fact]
    public void ResolveTargets_Broadcasts_WhenMessageTargetsAll()
    {
        var router = CreateRouter(
            runners: [new FakeRunner("default"), new FakeRunner("planner"), new FakeRunner("writer")]);

        var message = MessageWithMetadata(("agent", "all"));
        var targets = router.ResolveTargets(message);

        targets.Should().HaveCount(3);
        targets.Select(t => t.AgentName).Should().BeEquivalentTo(["default", "planner", "writer"]);
    }

    [Fact]
    public void ResolveTargets_UsesConfiguredDefault_WhenAgentIsUnspecified()
    {
        var router = CreateRouter(
            runners: [new FakeRunner("default"), new FakeRunner("planner")],
            cfg => cfg.Gateway.DefaultAgent = "planner");

        var targets = router.ResolveTargets(MessageWithMetadata());

        targets.Should().ContainSingle();
        targets[0].AgentName.Should().Be("planner");
    }

    [Fact]
    public void ResolveTargets_BroadcastsWhenNoAgentSpecified_AndBroadcastEnabled()
    {
        var router = CreateRouter(
            runners: [new FakeRunner("default"), new FakeRunner("planner"), new FakeRunner("writer")],
            cfg =>
            {
                cfg.Gateway.DefaultAgent = "planner";
                cfg.Gateway.BroadcastWhenAgentUnspecified = true;
            });

        var targets = router.ResolveTargets(MessageWithMetadata());

        targets.Should().HaveCount(3);
        targets.Select(t => t.AgentName).Should().BeEquivalentTo(["default", "planner", "writer"]);
    }

    private static AgentRouter CreateRouter(
        IReadOnlyList<IAgentRunner> runners,
        Action<BotNexusConfig>? configure = null)
    {
        var cfg = new BotNexusConfig();
        configure?.Invoke(cfg);
        return new AgentRouter(
            runners,
            Options.Create(cfg),
            NullLogger<AgentRouter>.Instance);
    }

    private static InboundMessage MessageWithMetadata(params (string Key, object Value)[] metadata)
    {
        var values = metadata.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        return new InboundMessage(
            Channel: "test",
            SenderId: "user",
            ChatId: "chat",
            Content: "hello",
            Timestamp: DateTimeOffset.UtcNow,
            Media: [],
            Metadata: values);
    }

    private sealed class FakeRunner(string agentName) : IAgentRunner
    {
        public string AgentName { get; } = agentName;

        public Task RunAsync(InboundMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
