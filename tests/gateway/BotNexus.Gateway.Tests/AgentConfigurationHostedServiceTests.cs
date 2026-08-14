using System.Text.Json;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Security;
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

        // Zero debounce still applies via a background Task.Delay(0).ContinueWith continuation.
        // Await the registry's own registration signal rather than a wall-clock budget (#3155).
        await registry.WaitForRegistrationAsync("agent-c");

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
        // Await the background apply continuation via the registry's registration signal (#3155).
        await registry.WaitForRegistrationAsync("agent-new");

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

    [Fact]
    public async Task OnSourceChange_FileAccessChanged_ReRegistersAgent()
    {
        await AssertReRegistersOnChangeAsync(d => d with
        {
            FileAccess = new FileAccessPolicy { AllowedReadPaths = ["/srv/extra"] }
        });
    }

    [Fact]
    public async Task OnSourceChange_FileAccessAllowedWritePathAdded_ReRegistersAgent()
    {
        await AssertReRegistersOnChangeAsync(d => d with
        {
            FileAccess = new FileAccessPolicy { AllowedWritePaths = ["/srv/out"] }
        });
    }

    [Fact]
    public async Task OnSourceChange_FileAccessDeniedPathAdded_ReRegistersAgent()
    {
        await AssertReRegistersOnChangeAsync(d => d with
        {
            FileAccess = new FileAccessPolicy { DeniedPaths = ["/etc/secrets"] }
        });
    }

    [Fact]
    public async Task OnSourceChange_MetadataChanged_ReRegistersAgent()
    {
        await AssertReRegistersOnChangeAsync(d => d with
        {
            Metadata = new Dictionary<string, object?> { ["role"] = "reviewer" }
        });
    }

    [Fact]
    public async Task OnSourceChange_MemoryConfigChanged_ReRegistersAgent()
    {
        await AssertReRegistersOnChangeAsync(d => d with
        {
            Memory = new MemoryAgentConfig { Enabled = true, Path = "memory" }
        });
    }

    [Fact]
    public async Task OnSourceChange_SoulConfigChanged_ReRegistersAgent()
    {
        await AssertReRegistersOnChangeAsync(d => d with
        {
            Soul = new SoulAgentConfig { Enabled = true, Timezone = "Europe/London" }
        });
    }

    [Fact]
    public async Task OnSourceChange_HeartbeatConfigChanged_ReRegistersAgent()
    {
        await AssertReRegistersOnChangeAsync(d => d with
        {
            Heartbeat = new HeartbeatAgentConfig { Enabled = true, IntervalMinutes = 5 }
        });
    }

    [Fact]
    public async Task OnSourceChange_IsolationOptionsChanged_ReRegistersAgent()
    {
        await AssertReRegistersOnChangeAsync(d => d with
        {
            IsolationOptions = new Dictionary<string, object?> { ["image"] = "custom:2" }
        });
    }

    [Fact]
    public async Task OnSourceChange_ExtensionConfigChanged_ReRegistersAgent()
    {
        using var document = JsonDocument.Parse("""{"enabled":true}""");
        var element = document.RootElement.Clone();
        await AssertReRegistersOnChangeAsync(d => d with
        {
            ExtensionConfig = new Dictionary<string, JsonElement> { ["botnexus-skills"] = element }
        });
    }

    /// <summary>
    /// Applies <paramref name="mutate"/> to a baseline descriptor and asserts that the hosted service
    /// treats the mutated descriptor as changed: it unregisters and re-registers the agent so the
    /// downstream runtime (e.g. the agent's IPathValidator) is rebuilt from the new descriptor.
    /// </summary>
    private static async Task AssertReRegistersOnChangeAsync(Func<AgentDescriptor, AgentDescriptor> mutate)
    {
        var baseline = CreateDescriptor("agent-a");
        var mutated = mutate(baseline);

        var source = new Mock<IAgentConfigurationSource>();
        Action<IReadOnlyList<AgentDescriptor>>? callback = null;
        source.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([baseline]);
        source.Setup(s => s.Watch(It.IsAny<Action<IReadOnlyList<AgentDescriptor>>>()))
            .Callback<Action<IReadOnlyList<AgentDescriptor>>>(cb => callback = cb)
            .Returns(Mock.Of<IDisposable>());
        var registry = new RecordingAgentRegistry();
        var service = new AgentConfigurationHostedService([source.Object], registry, NullLogger<AgentConfigurationHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        registry.RegisterOperations.Clear();
        registry.UnregisterOperations.Clear();
        callback.ShouldNotBeNull();

        callback!([mutated]);

        await registry.WaitForRegistrationAsync("agent-a");

        registry.UnregisterOperations.ShouldContain("agent-a");
        registry.RegisterOperations.ShouldContain("agent-a");
        registry.Get(AgentId.From("agent-a")).ShouldBe(mutated);
    }

    [Fact]
    public async Task OnSourceChange_ConversationRetentionChanged_ReRegistersAgent()
    {
        await AssertReRegistersOnChangeAsync(d => d with
        {
            ConversationRetention = new AgentConversationRetentionConfig { AutoArchiveAfterDays = 7 }
        });
    }

    [Fact]
    public async Task OnSourceChange_DateTimeInjectionChanged_ReRegistersAgent()
    {
        await AssertReRegistersOnChangeAsync(d => d with
        {
            DateTimeInjection = new DateTimeInjectionConfig { Enabled = true, Timezone = "Europe/London" }
        });
    }

    [Fact]
    public async Task OnSourceChange_OrderChanged_ReRegistersAgent()
    {
        await AssertReRegistersOnChangeAsync(d => d with { Order = 42 });
    }

    [Fact]
    public async Task OnSourceChange_SystemPromptChanged_ReRegistersAgent()
    {
        await AssertReRegistersOnChangeAsync(d => d with { SystemPrompt = "you are new" });
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
        /// <summary>
        /// Upper bound on how long <see cref="WaitForRegistrationAsync"/> will wait.
        /// <para>
        /// This is a <b>hang guard, not a latency assertion.</b> The registration signal is raised
        /// synchronously from <see cref="Register"/>, so in a healthy run the wait completes as soon
        /// as the hosted service's apply continuation is scheduled - however long the runner takes
        /// to get to it. This ceiling exists solely so that a genuinely stuck service fails the test
        /// instead of hanging the suite forever, and is deliberately far larger than any plausible
        /// threadpool scheduling delay on a contended CI runner. A slow machine makes this test
        /// slower, never red. Do not reduce it to "tighten" the test: a tight wall-clock budget
        /// encodes machine speed into a correctness assertion, which is precisely the defect fixed
        /// in #3155 (a 5000 ms budget inside an assembly that takes over two minutes).
        /// </para>
        /// </summary>
        private static readonly TimeSpan RegistrationHangGuard = TimeSpan.FromMinutes(2);

        private readonly Dictionary<string, AgentDescriptor> _agents;
        private readonly Lock _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _registrationSignals =
            new(StringComparer.OrdinalIgnoreCase);

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

        /// <summary>
        /// Completes once <paramref name="agentId"/> has been registered - either already, or by a
        /// later <see cref="Register"/> call, which completes the waiter directly.
        /// <para>
        /// This is a deterministic signal owned by the test double: the test observes the exact
        /// outcome it asserts on (the re-registration) instead of polling a clock and hoping the
        /// machine is fast enough. The wait is non-vacuous - if the hosted service never
        /// re-registers, no signal is ever raised and the wait fails against
        /// <see cref="RegistrationHangGuard"/>.
        /// </para>
        /// </summary>
        public async Task WaitForRegistrationAsync(string agentId)
        {
            Task signal;
            lock (_gate)
            {
                if (RegisterOperations.Contains(agentId, StringComparer.OrdinalIgnoreCase))
                    return;

                if (!_registrationSignals.TryGetValue(agentId, out var tcs))
                {
                    tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _registrationSignals[agentId] = tcs;
                }

                signal = tcs.Task;
            }

            using var guard = new CancellationTokenSource(RegistrationHangGuard);
            var completed = await Task.WhenAny(signal, Task.Delay(Timeout.Infinite, guard.Token))
                .ConfigureAwait(false);
            if (!ReferenceEquals(completed, signal))
            {
                throw new TimeoutException(
                    $"Agent '{agentId}' was never registered within the {RegistrationHangGuard.TotalMinutes:0}-minute " +
                    "hang guard. This ceiling is not a latency budget: exceeding it means the hosted service is " +
                    "stuck or never applied the configuration change at all.");
            }

            await signal.ConfigureAwait(false);
        }

        public void Register(AgentDescriptor descriptor)
        {
            TaskCompletionSource? signal = null;
            lock (_gate)
            {
                if (_agents.ContainsKey(descriptor.AgentId.Value))
                    throw new InvalidOperationException($"Agent '{descriptor.AgentId}' already exists.");

                _agents[descriptor.AgentId.Value] = descriptor;
                RegisterOperations.Add(descriptor.AgentId.Value);

                if (_registrationSignals.Remove(descriptor.AgentId.Value, out var tcs))
                    signal = tcs;
            }

            signal?.TrySetResult();
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
