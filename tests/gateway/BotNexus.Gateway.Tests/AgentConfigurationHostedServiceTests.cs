using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class AgentConfigurationHostedServiceTests
{
    [Fact]
    public async Task StartAsync_WithMultipleSources_RegistersDescriptorsFromAllSources()
    {
        var sourceA = new Mock<IAgentConfigurationSource>();
        var sourceB = new Mock<IAgentConfigurationSource>();
        sourceA.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateDescriptor("agent-a")]);
        sourceB.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateDescriptor("agent-b")]);
        sourceA.Setup(s => s.Watch(It.IsAny<Action<IReadOnlyList<AgentDescriptor>>>()))
            .Returns(Mock.Of<IDisposable>());
        sourceB.Setup(s => s.Watch(It.IsAny<Action<IReadOnlyList<AgentDescriptor>>>()))
            .Returns(Mock.Of<IDisposable>());
        var registry = new RecordingAgentRegistry();
        var service = new AgentConfigurationHostedService([sourceA.Object, sourceB.Object], registry, NullLogger<AgentConfigurationHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        registry.GetAll().Select(d => d.AgentId.Value).ShouldBe(new[] { "agent-a", "agent-b" });
    }

    [Fact]
    public async Task StartAsync_WithCodeBasedDescriptor_SkipsShadowedConfigAgent()
    {
        var source = new Mock<IAgentConfigurationSource>();
        source.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateDescriptor("code-agent"), CreateDescriptor("config-agent")]);
        source.Setup(s => s.Watch(It.IsAny<Action<IReadOnlyList<AgentDescriptor>>>()))
            .Returns(Mock.Of<IDisposable>());
        var registry = new RecordingAgentRegistry([CreateDescriptor("code-agent")]);
        var service = new AgentConfigurationHostedService([source.Object], registry, NullLogger<AgentConfigurationHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        registry.Contains("code-agent").ShouldBeTrue();
        registry.Contains("config-agent").ShouldBeTrue();
        registry.RegisterOperations.Where(o => o == "config-agent").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task StartAsync_OnSourceChange_ReRegistersAddedModifiedAndRemovedAgents()
    {
        var source = new Mock<IAgentConfigurationSource>();
        Action<IReadOnlyList<AgentDescriptor>>? callback = null;
        source.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateDescriptor("agent-a", "Agent A v1"), CreateDescriptor("agent-b")]);
        source.Setup(s => s.Watch(It.IsAny<Action<IReadOnlyList<AgentDescriptor>>>()))
            .Callback<Action<IReadOnlyList<AgentDescriptor>>>(cb => callback = cb)
            .Returns(Mock.Of<IDisposable>());
        var registry = new RecordingAgentRegistry();
        var service = new AgentConfigurationHostedService([source.Object], registry, NullLogger<AgentConfigurationHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        callback.ShouldNotBeNull();

        callback!(
        [
            CreateDescriptor("agent-a", "Agent A v2"),
            CreateDescriptor("agent-c")
        ]);

        registry.Contains("agent-a").ShouldBeTrue();
        registry.Get("agent-a")!.DisplayName.ShouldBe("Agent A v2");
        registry.Contains("agent-b").ShouldBeFalse();
        registry.Contains("agent-c").ShouldBeTrue();
        registry.UnregisterOperations.ShouldContain("agent-b");
        registry.UnregisterOperations.ShouldContain("agent-a");
        registry.RegisterOperations.ShouldContain("agent-a");
        registry.RegisterOperations.ShouldContain("agent-b");
        registry.RegisterOperations.ShouldContain("agent-c");
    }

    [Fact]
    public async Task StopAsync_WithActiveWatchers_DisposesWatchers()
    {
        var watcher = new Mock<IDisposable>();
        var source = new Mock<IAgentConfigurationSource>();
        source.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateDescriptor("agent-a")]);
        source.Setup(s => s.Watch(It.IsAny<Action<IReadOnlyList<AgentDescriptor>>>()))
            .Returns(watcher.Object);
        var registry = new RecordingAgentRegistry();
        var service = new AgentConfigurationHostedService([source.Object], registry, NullLogger<AgentConfigurationHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        watcher.Verify(w => w.Dispose(), Times.Once);
    }

    [Fact]
    public async Task StartAsync_WithNoSources_DoesNotRegisterAgents()
    {
        var registry = new RecordingAgentRegistry();
        var service = new AgentConfigurationHostedService([], registry, NullLogger<AgentConfigurationHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        registry.RegisterOperations.ShouldBeEmpty();
    }

    private static AgentDescriptor CreateDescriptor(string agentId, string? displayName = null)
        => new()
        {
            AgentId = AgentId.From(agentId),
            DisplayName = displayName ?? $"Display {agentId}",
            ModelId = "model",
            ApiProvider = "provider"
        };

    private sealed class RecordingAgentRegistry : IAgentRegistry
    {
        private readonly Dictionary<string, AgentDescriptor> _agents;

        public RecordingAgentRegistry(IEnumerable<AgentDescriptor>? initialDescriptors = null)
        {
            _agents = new Dictionary<string, AgentDescriptor>(StringComparer.OrdinalIgnoreCase);
            if (initialDescriptors is not null)
            {
                foreach (var descriptor in initialDescriptors)
                    _agents[descriptor.AgentId] = descriptor;
            }
        }

        public List<string> RegisterOperations { get; } = [];

        public List<string> UnregisterOperations { get; } = [];

        public void Register(AgentDescriptor descriptor)
        {
            if (_agents.ContainsKey(descriptor.AgentId))
                throw new InvalidOperationException($"Agent '{descriptor.AgentId}' already exists.");

            _agents[descriptor.AgentId] = descriptor;
            RegisterOperations.Add(descriptor.AgentId);
        }

        public void Unregister(AgentId agentId)
        {
            _agents.Remove(agentId);
            UnregisterOperations.Add(agentId);
        }

        public AgentDescriptor? Get(AgentId agentId)
            => _agents.TryGetValue(agentId, out var descriptor) ? descriptor : null;

        public IReadOnlyList<AgentDescriptor> GetAll()
            => _agents.Values.ToArray();

        public bool Contains(AgentId agentId)
            => _agents.ContainsKey(agentId);
    }
}
