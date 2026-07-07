using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class AgentConfigurationHostedServiceTests : IDisposable
{
    private readonly TimeSpan _originalDebounce;

    public AgentConfigurationHostedServiceTests()
    {
        _originalDebounce = AgentConfigurationHostedService.DebounceDelay;
        // Default tests run without debounce for deterministic behavior
        AgentConfigurationHostedService.DebounceDelay = TimeSpan.Zero;
    }

    public void Dispose()
    {
        AgentConfigurationHostedService.DebounceDelay = _originalDebounce;
    }

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

        registry.Contains(AgentId.From("code-agent")).ShouldBeTrue();
        registry.Contains(AgentId.From("config-agent")).ShouldBeTrue();
        registry.RegisterOperations.Where(o => o == "config-agent").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task OnSourceChange_ReRegistersAddedModifiedAndRemovedAgents()
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

        // Zero debounce still applies via a background Task.Delay(0).ContinueWith continuation;
        // poll deterministically instead of a fixed sleep so a starved CI threadpool cannot flake.
        await WaitUntilAsync(() => registry.RegisterOperations.Contains("agent-c"));

        registry.Contains(AgentId.From("agent-a")).ShouldBeTrue();
        registry.Get(AgentId.From("agent-a"))!.DisplayName.ShouldBe("Agent A v2");
        registry.Contains(AgentId.From("agent-b")).ShouldBeFalse();
        registry.Contains(AgentId.From("agent-c")).ShouldBeTrue();
        registry.UnregisterOperations.ShouldContain("agent-b");
        registry.UnregisterOperations.ShouldContain("agent-a");
        registry.RegisterOperations.ShouldContain("agent-a");
        registry.RegisterOperations.ShouldContain("agent-b");
        registry.RegisterOperations.ShouldContain("agent-c");
    }

    [Fact]
    public async Task OnSourceChange_AddsNewAgentWithoutRestart()
    {
        var source = new Mock<IAgentConfigurationSource>();
        Action<IReadOnlyList<AgentDescriptor>>? callback = null;
        source.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        source.Setup(s => s.Watch(It.IsAny<Action<IReadOnlyList<AgentDescriptor>>>()))
            .Callback<Action<IReadOnlyList<AgentDescriptor>>>(cb => callback = cb)
            .Returns(Mock.Of<IDisposable>());
        var registry = new RecordingAgentRegistry();
        var service = new AgentConfigurationHostedService([source.Object], registry, NullLogger<AgentConfigurationHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        registry.GetAll().ShouldBeEmpty();
        callback.ShouldNotBeNull();

        callback!([CreateDescriptor("agent-new")]);
        // Poll for the background apply continuation instead of a fixed sleep (CI-threadpool-safe).
        await WaitUntilAsync(() => registry.RegisterOperations.Contains("agent-new"));

        registry.Contains(AgentId.From("agent-new")).ShouldBeTrue();
        registry.RegisterOperations.ShouldContain("agent-new");
    }

    [Fact]
    public async Task OnSourceChange_UnchangedDescriptors_DoesNotReRegister()
    {
        var source = new Mock<IAgentConfigurationSource>();
        Action<IReadOnlyList<AgentDescriptor>>? callback = null;
        var descriptor = CreateDescriptor("agent-a", "Stable Agent");
        source.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([descriptor]);
        source.Setup(s => s.Watch(It.IsAny<Action<IReadOnlyList<AgentDescriptor>>>()))
            .Callback<Action<IReadOnlyList<AgentDescriptor>>>(cb => callback = cb)
            .Returns(Mock.Of<IDisposable>());
        var registry = new RecordingAgentRegistry();
        var service = new AgentConfigurationHostedService([source.Object], registry, NullLogger<AgentConfigurationHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        registry.RegisterOperations.Clear();
        registry.UnregisterOperations.Clear();
        callback.ShouldNotBeNull();

        // Fire change with an identical descriptor (new instance, same values)
        callback!([CreateDescriptor("agent-a", "Stable Agent")]);
        await Task.Delay(200);

        registry.UnregisterOperations.ShouldBeEmpty();
        // Should NOT re-register — agent unchanged
        registry.RegisterOperations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Debounce_RapidFireNotifications_CoalescedIntoSingleApply()
    {
        AgentConfigurationHostedService.DebounceDelay = TimeSpan.FromMilliseconds(200);

        var source = new Mock<IAgentConfigurationSource>();
        Action<IReadOnlyList<AgentDescriptor>>? callback = null;
        source.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        source.Setup(s => s.Watch(It.IsAny<Action<IReadOnlyList<AgentDescriptor>>>()))
            .Callback<Action<IReadOnlyList<AgentDescriptor>>>(cb => callback = cb)
            .Returns(Mock.Of<IDisposable>());
        var registry = new RecordingAgentRegistry();
        var service = new AgentConfigurationHostedService([source.Object], registry, NullLogger<AgentConfigurationHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        callback.ShouldNotBeNull();

        // Fire 10 rapid notifications — only the last should apply
        for (int i = 0; i < 10; i++)
        {
            callback!([CreateDescriptor($"agent-{i}")]);
        }

        // Before debounce fires — nothing should be registered
        registry.RegisterOperations.ShouldBeEmpty();

        // Wait for debounce to fire (generous margin for slow CI runners)
        await Task.Delay(1000);

        // Only the final state should be applied (agent-9 from the last call)
        registry.Contains(AgentId.From("agent-9")).ShouldBeTrue();
        // Earlier intermediate states should NOT be registered (they were overwritten)
        registry.Contains(AgentId.From("agent-0")).ShouldBeFalse();
        registry.Contains(AgentId.From("agent-5")).ShouldBeFalse();
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
    public async Task StopAsync_CancelsPendingDebounce()
    {
        AgentConfigurationHostedService.DebounceDelay = TimeSpan.FromMilliseconds(500);

        var source = new Mock<IAgentConfigurationSource>();
        Action<IReadOnlyList<AgentDescriptor>>? callback = null;
        source.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        source.Setup(s => s.Watch(It.IsAny<Action<IReadOnlyList<AgentDescriptor>>>()))
            .Callback<Action<IReadOnlyList<AgentDescriptor>>>(cb => callback = cb)
            .Returns(Mock.Of<IDisposable>());
        var registry = new RecordingAgentRegistry();
        var service = new AgentConfigurationHostedService([source.Object], registry, NullLogger<AgentConfigurationHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        callback.ShouldNotBeNull();

        // Schedule a change but stop before debounce fires
        callback!([CreateDescriptor("agent-pending")]);
        await service.StopAsync(CancellationToken.None);

        // Wait past the debounce window
        await Task.Delay(700);

        // Should NOT have been applied (stop cancelled it)
        registry.Contains(AgentId.From("agent-pending")).ShouldBeFalse();
    }

    [Fact]
    public async Task StartAsync_WithNoSources_DoesNotRegisterAgents()
    {
        var registry = new RecordingAgentRegistry();
        var service = new AgentConfigurationHostedService([], registry, NullLogger<AgentConfigurationHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        registry.RegisterOperations.ShouldBeEmpty();
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it returns true or the timeout elapses. Used to await
    /// the service's background debounce/apply continuation deterministically. A fixed Task.Delay is
    /// flaky on CI: the zero-delay apply runs on a threadpool continuation that can be starved, so a
    /// hard-coded sleep occasionally expires before the apply lands (root cause of #1800).
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000, int pollMs = 10)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Condition was not met within {timeoutMs}ms.");
            await Task.Delay(pollMs);
        }
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
                    _agents[descriptor.AgentId.Value] = descriptor;
            }
        }

        public List<string> RegisterOperations { get; } = [];

        public List<string> UnregisterOperations { get; } = [];

        public void Register(AgentDescriptor descriptor)
        {
            if (_agents.ContainsKey(descriptor.AgentId.Value))
                throw new InvalidOperationException($"Agent '{descriptor.AgentId}' already exists.");

            _agents[descriptor.AgentId.Value] = descriptor;
            RegisterOperations.Add(descriptor.AgentId.Value);
        }

        public void Unregister(AgentId agentId)
        {
            _agents.Remove(agentId.Value);
            UnregisterOperations.Add(agentId.Value);
        }

        public AgentDescriptor? Get(AgentId agentId)
            => _agents.TryGetValue(agentId.Value, out var descriptor) ? descriptor : null;

        public IReadOnlyList<AgentDescriptor> GetAll()
            => _agents.Values.ToArray();

        public bool Contains(AgentId agentId)
            => _agents.ContainsKey(agentId.Value);
    }
}
