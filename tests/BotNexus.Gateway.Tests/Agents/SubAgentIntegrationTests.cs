using System.Reflection;
using BotNexus.AgentCore;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Isolation;
using BotNexus.Gateway.Tools;
using BotNexus.Memory;
using BotNexus.Memory.Models;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class SubAgentIntegrationTests
{
    [Fact]
    public async Task SpawnAndList_ReturnsSpawnedSubAgent()
    {
        var registry = CreateRegistry();
        var supervisor = CreateSupervisor(CreateHangingHandle().Object);
        var manager = CreateManager(supervisor.Object, registry.Object);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        var listed = await manager.ListAsync("parent-session");

        listed.Should().ContainSingle(info => info.SubAgentId == spawned.SubAgentId);
    }

    [Fact]
    public async Task SpawnAndKill_UpdatesStatus()
    {
        var registry = CreateRegistry();
        var supervisor = CreateSupervisor(CreateHangingHandle().Object);
        supervisor.Setup(s => s.StopAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var manager = CreateManager(supervisor.Object, registry.Object);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        var killed = await manager.KillAsync(spawned.SubAgentId, spawned.ParentSessionId);
        var updated = await manager.GetAsync(spawned.SubAgentId);

        killed.Should().BeTrue();
        updated.Should().NotBeNull();
        updated!.Status.Should().BeOneOf(SubAgentStatus.Killed, SubAgentStatus.TimedOut);
    }

    [Fact]
    public async Task Spawn_EnforcesConcurrentLimit()
    {
        var registry = CreateRegistry();
        var supervisor = CreateSupervisor(CreateHangingHandle().Object);
        var manager = CreateManager(
            supervisor.Object,
            registry.Object,
            new GatewayOptions { SubAgents = new SubAgentOptions { MaxConcurrentPerSession = 1 } });

        _ = await manager.SpawnAsync(CreateSpawnRequest());
        var act = () => manager.SpawnAsync(CreateSpawnRequest());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*running sub-agents*");
    }

    [Fact]
    public async Task Kill_OnlyAllowedByParent()
    {
        var registry = CreateRegistry();
        var supervisor = CreateSupervisor(CreateHangingHandle().Object);
        var manager = CreateManager(supervisor.Object, registry.Object);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        var killed = await manager.KillAsync(spawned.SubAgentId, "different-session");
        var updated = await manager.GetAsync(spawned.SubAgentId);

        killed.Should().BeFalse();
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(SubAgentStatus.Running);
    }

    [Fact]
    public async Task SubAgentSession_DoesNotGetSpawnTools()
    {
        var strategy = CreateStrategy();
        var descriptor = CreateDescriptor();

        var parentHandle = await strategy.CreateAsync(descriptor, new AgentExecutionContext { SessionId = BotNexus.Domain.Primitives.SessionId.From("parent-session") });
        var parentToolNames = GetToolNames(parentHandle);

        var subAgentHandle = await strategy.CreateAsync(descriptor, new AgentExecutionContext { SessionId = BotNexus.Domain.Primitives.SessionId.From("parent-session::subagent::child") });
        var subAgentToolNames = GetToolNames(subAgentHandle);

        parentToolNames.Should().Contain("spawn_subagent");
        parentToolNames.Should().Contain("list_subagents");
        parentToolNames.Should().Contain("manage_subagent");
        subAgentToolNames.Should().NotContain(["spawn_subagent", "list_subagents", "manage_subagent"]);
    }

    private static DefaultSubAgentManager CreateManager(
        IAgentSupervisor supervisor,
        IAgentRegistry registry,
        GatewayOptions? options = null)
        => new(
            supervisor,
            registry,
            new Mock<IActivityBroadcaster>().Object,
            new Mock<IChannelDispatcher>().Object,
            Options.Create(options ?? new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance);

    private static SubAgentSpawnRequest CreateSpawnRequest()
        => new()
        {
            ParentAgentId = BotNexus.Domain.Primitives.AgentId.From("parent-agent"),
            ParentSessionId = BotNexus.Domain.Primitives.SessionId.From("parent-session"),
            Task = "Investigate flaky test"
        };

    private static Mock<IAgentRegistry> CreateRegistry()
    {
        var descriptor = CreateDescriptor();
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get("parent-agent")).Returns(descriptor);
        return registry;
    }

    private static AgentDescriptor CreateDescriptor()
        => new()
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("parent-agent"),
            DisplayName = "Parent Agent",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            SystemPrompt = "You are a test agent.",
            ToolIds = ["spawn_subagent", "list_subagents", "manage_subagent"]
        };

    private static Mock<IAgentSupervisor> CreateSupervisor(IAgentHandle childHandle)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle);
        return supervisor;
    }

    private static Mock<IAgentHandle> CreateHangingHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("parent-agent");
        handle.SetupGet(h => h.SessionId).Returns("child-session");
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new AgentResponse { Content = "never" };
            });
        return handle;
    }

    private static InProcessIsolationStrategy CreateStrategy()
    {
        var modelRegistry = new ModelRegistry();
        modelRegistry.Register("test-provider", new LlmModel(
            Id: "test-model",
            Name: "test-model",
            Api: "test-api",
            Provider: "test-provider",
            BaseUrl: "http://localhost",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 8192,
            MaxTokens: 1024));

        var services = new ServiceCollection();
        services.AddSingleton<ISubAgentManager>(new Mock<ISubAgentManager>().Object);
        services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(new GatewayOptions
        {
            SubAgents = new SubAgentOptions
            {
                MaxDepth = 1
            }
        }));

        return new InProcessIsolationStrategy(
            new LlmClient(new ApiProviderRegistry(), modelRegistry),
            new GatewayAuthManager(new PlatformConfig(), NullLogger<GatewayAuthManager>.Instance, new FileSystem()),
            new PassthroughContextBuilder(),
            new EmptyToolFactory(),
            new TestWorkspaceManager(),
            new DefaultToolRegistry([]),
            new StubMemoryStoreFactory(),
            services.BuildServiceProvider(),
            NullLogger<InProcessIsolationStrategy>.Instance);
    }

    private static IReadOnlyList<string> GetToolNames(IAgentHandle handle)
    {
        var agentField = handle.GetType().GetField("_agent", BindingFlags.Instance | BindingFlags.NonPublic);
        agentField.Should().NotBeNull();

        var agent = agentField!.GetValue(handle) as Agent;
        agent.Should().NotBeNull();

        return [.. agent!.State.Tools.Select(tool => tool.Name)];
    }

    private sealed class PassthroughContextBuilder : IContextBuilder
    {
        public Task<string> BuildSystemPromptAsync(AgentDescriptor descriptor, CancellationToken ct = default)
            => Task.FromResult(descriptor.SystemPrompt ?? string.Empty);
    }

    private sealed class EmptyToolFactory : IAgentToolFactory
    {
        public IReadOnlyList<IAgentTool> CreateTools(string workingDirectory, IPathValidator? pathValidator = null) => [];
    }

    private sealed class TestWorkspaceManager : IAgentWorkspaceManager
    {
        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentWorkspace(agentName, string.Empty, string.Empty, string.Empty, string.Empty));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public string GetWorkspacePath(string agentName)
            => AppContext.BaseDirectory;
    }

    private sealed class StubMemoryStoreFactory : IMemoryStoreFactory
    {
        private readonly IMemoryStore _store = new StubMemoryStore();
        public IMemoryStore Create(string agentId) => _store;
    }

    private sealed class StubMemoryStore : IMemoryStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<MemoryEntry> InsertAsync(MemoryEntry entry, CancellationToken ct = default) => Task.FromResult(entry);
        public Task<MemoryEntry?> GetByIdAsync(string id, CancellationToken ct = default) => Task.FromResult<MemoryEntry?>(null);
        public Task<IReadOnlyList<MemoryEntry>> GetBySessionAsync(string sessionId, int limit = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<MemoryStoreStats> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(new MemoryStoreStats(0, 0, null));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
