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

    [Fact]
    public void ReloadFromConfig_RecreatesAffectedAgents_AndRemovesDeletedAgents()
    {
        var previous = new BotNexusConfig();
        previous.Agents.Named["planner"] = new AgentConfig { Model = "gpt-4o-mini" };
        previous.Agents.Named["writer"] = new AgentConfig { Model = "gpt-4o" };

        var current = new BotNexusConfig();
        current.Agents.Named["planner"] = new AgentConfig { Model = "gpt-5" };
        current.Agents.Named["reviewer"] = new AgentConfig { Model = "gpt-4.1" };
        current.Gateway.DefaultAgent = "reviewer";

        var created = new List<string>();
        var runnerFactory = new TestRunnerFactory(created);

        var router = new AgentRouter(
            runners: [new FakeRunner("planner"), new FakeRunner("writer")],
            config: new TestOptionsMonitor(previous),
            logger: NullLogger<AgentRouter>.Instance,
            runnerFactory: runnerFactory);

        router.ReloadFromConfig(previous, current, ["planner", "reviewer"]);

        created.Should().BeEquivalentTo(["planner", "reviewer"]);
        var fallback = router.ResolveTargets(MessageWithMetadata(("agent", "writer")));
        fallback.Should().ContainSingle();
        fallback[0].AgentName.Should().Be("reviewer");
        var reviewer = router.ResolveTargets(MessageWithMetadata(("agent", "reviewer")));
        reviewer.Should().ContainSingle();
        reviewer[0].AgentName.Should().Be("reviewer");
    }

    private static AgentRouter CreateRouter(
        IReadOnlyList<IAgentRunner> runners,
        Action<BotNexusConfig>? configure = null)
    {
        var cfg = new BotNexusConfig();
        configure?.Invoke(cfg);
        return new AgentRouter(
            runners,
            new TestOptionsMonitor(cfg),
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

    private sealed class TestRunnerFactory(List<string> created) : IAgentRunnerFactory
    {
        public IAgentRunner Create(string agentName)
        {
            created.Add(agentName);
            return new FakeRunner(agentName);
        }
    }

    private sealed class TestOptionsMonitor(BotNexusConfig value) : IOptionsMonitor<BotNexusConfig>
    {
        public BotNexusConfig CurrentValue { get; private set; } = value;
        public BotNexusConfig Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<BotNexusConfig, string?> listener) => NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}
