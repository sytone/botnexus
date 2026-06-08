using System.Reflection;
using BotNexus.Domain.Primitives;
using BotNexus.Agent.Core;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Isolation;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tools;
using BotNexus.Memory;
using BotNexus.Memory.Models;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
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
        var listed = await manager.ListAsync(SessionId.From("parent-session"));

        listed.Where(info => info.SubAgentId == spawned.SubAgentId).ShouldHaveSingleItem();
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

        killed.ShouldBeTrue();
        updated.ShouldNotBeNull();
        updated!.Status.ShouldBeOneOf(SubAgentStatus.Killed, SubAgentStatus.TimedOut);
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
        Func<Task> act = () => manager.SpawnAsync(CreateSpawnRequest());

        (await act.ShouldThrowAsync<InvalidOperationException>())
            .Message.ShouldContain("running sub-agents");
    }

    [Fact]
    public async Task Kill_OnlyAllowedByParent()
    {
        var registry = CreateRegistry();
        var supervisor = CreateSupervisor(CreateHangingHandle().Object);
        var manager = CreateManager(supervisor.Object, registry.Object);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        var killed = await manager.KillAsync(spawned.SubAgentId, SessionId.From("different-session"));
        var updated = await manager.GetAsync(spawned.SubAgentId);

        killed.ShouldBeFalse();
        updated.ShouldNotBeNull();
        updated!.Status.ShouldBe(SubAgentStatus.Running);
    }

    [Fact]
    public async Task SubAgentSession_DoesNotGetSpawnTools()
    {
        // Parent session is bound to a conversation → spawn tool registered.
        // Sub-agent session has no spawn/list/manage tools regardless of binding
        // (security: prevents recursive spawning).
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        await SeedBoundSessionAsync(
            conversationStore,
            sessionStore,
            agentId: AgentId.From("parent-agent"),
            sessionId: SessionId.From("parent-session"),
            conversationId: ConversationId.From("parent-conv"));

        var strategy = CreateStrategy(conversationStore: conversationStore, sessionStore: sessionStore);
        var descriptor = CreateDescriptor();

        var parentHandle = await strategy.CreateAsync(descriptor, new AgentExecutionContext { SessionId = BotNexus.Domain.Primitives.SessionId.From("parent-session") });
        var parentToolNames = GetToolNames(parentHandle);

        var subAgentHandle = await strategy.CreateAsync(descriptor, new AgentExecutionContext { SessionId = BotNexus.Domain.Primitives.SessionId.From("parent-session::subagent::child") });
        var subAgentToolNames = GetToolNames(subAgentHandle);

        parentToolNames.ShouldContain("spawn_subagent");
        parentToolNames.ShouldContain("list_subagents");
        parentToolNames.ShouldContain("manage_subagent");
        subAgentToolNames.ShouldNotContain("spawn_subagent");
        subAgentToolNames.ShouldNotContain("list_subagents");
        subAgentToolNames.ShouldNotContain("manage_subagent");
    }

    [Fact]
    public async Task Parent_WithoutBoundConversation_DoesNotGetSpawnTool()
    {
        // F-6 contract: spawn_subagent must NEVER be registered for a session that
        // is not bound to a conversation. The sub-agent would otherwise inherit no
        // ConversationId and become orphaned in the portal thread.
        // list/manage still register — they only operate on already-spawned sub-agents.
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        // Deliberately do NOT seed a conversation binding.

        var strategy = CreateStrategy(conversationStore: conversationStore, sessionStore: sessionStore);
        var descriptor = CreateDescriptor();

        var handle = await strategy.CreateAsync(descriptor, new AgentExecutionContext { SessionId = BotNexus.Domain.Primitives.SessionId.From("unbound-session") });
        var toolNames = GetToolNames(handle);

        toolNames.ShouldNotContain("spawn_subagent",
            "spawn_subagent must not register for sessions without a bound conversation — " +
            "the child session would otherwise become orphaned with ConversationId == null.");
        toolNames.ShouldContain("list_subagents");
        toolNames.ShouldContain("manage_subagent");
    }

    [Fact]
    public async Task SpawnCompletion_StopsChildHandleAndCleansWorkspace()
    {
        var registry = CreateRegistry();
        var childHandle = new Mock<IAgentHandle>();
        childHandle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        childHandle.SetupGet(h => h.SessionId).Returns(SessionId.From("child-session"));
        childHandle.SetupGet(h => h.IsRunning).Returns(true);
        childHandle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "done" });

        var supervisor = CreateSupervisor(childHandle.Object);
        supervisor.Setup(s => s.StopAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var workspaceManager = new Mock<IAgentWorkspaceManager>();
        workspaceManager.Setup(w => w.TryCleanupWorkspace(It.IsAny<string>())).Returns(true);

        var manager = CreateManager(supervisor.Object, registry.Object, workspaceManager: workspaceManager.Object);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        await WaitUntilAsync(
            async () => (await manager.GetAsync(spawned.SubAgentId))?.Status == SubAgentStatus.Completed,
            TimeSpan.FromSeconds(2));
        var completed = await manager.GetAsync(spawned.SubAgentId);

        completed.ShouldNotBeNull();
        completed!.Status.ShouldBe(SubAgentStatus.Completed);
        supervisor.Verify(s => s.StopAsync(
                It.IsAny<BotNexus.Domain.Primitives.AgentId>(),
                completed.ChildSessionId,
                It.IsAny<CancellationToken>()),
            Times.Once);
        workspaceManager.Verify(w => w.TryCleanupWorkspace(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task SpawnFailure_CleansWorkspace()
    {
        var registry = CreateRegistry();
        var supervisor = CreateSupervisor(CreateFailingHandle().Object);
        supervisor.Setup(s => s.StopAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var workspaceManager = new Mock<IAgentWorkspaceManager>();
        workspaceManager.Setup(w => w.TryCleanupWorkspace(It.IsAny<string>())).Returns(true);

        var manager = CreateManager(supervisor.Object, registry.Object, workspaceManager: workspaceManager.Object);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        await WaitUntilAsync(
            async () => (await manager.GetAsync(spawned.SubAgentId))?.Status == SubAgentStatus.Failed,
            TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => Task.FromResult(workspaceManager.Invocations.Count > 0),
            TimeSpan.FromSeconds(2));

        workspaceManager.Verify(w => w.TryCleanupWorkspace(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task SpawnTimeout_CleansWorkspace()
    {
        var registry = CreateRegistry();
        var supervisor = CreateSupervisor(CreateHangingHandle().Object);
        supervisor.Setup(s => s.StopAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var workspaceManager = new Mock<IAgentWorkspaceManager>();
        workspaceManager.Setup(w => w.TryCleanupWorkspace(It.IsAny<string>())).Returns(true);

        var manager = CreateManager(supervisor.Object, registry.Object, workspaceManager: workspaceManager.Object);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest(timeoutSeconds: 1));

        await WaitUntilAsync(
            async () => (await manager.GetAsync(spawned.SubAgentId))?.Status == SubAgentStatus.TimedOut,
            TimeSpan.FromSeconds(3));
        await WaitUntilAsync(
            () => Task.FromResult(workspaceManager.Invocations.Count > 0),
            TimeSpan.FromSeconds(2));

        workspaceManager.Verify(w => w.TryCleanupWorkspace(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Kill_CleansWorkspace()
    {
        var registry = CreateRegistry();
        var supervisor = CreateSupervisor(CreateHangingHandle().Object);
        supervisor.Setup(s => s.StopAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var workspaceManager = new Mock<IAgentWorkspaceManager>();
        workspaceManager.Setup(w => w.TryCleanupWorkspace(It.IsAny<string>())).Returns(true);

        var manager = CreateManager(supervisor.Object, registry.Object, workspaceManager: workspaceManager.Object);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        var killed = await manager.KillAsync(spawned.SubAgentId, spawned.ParentSessionId);

        killed.ShouldBeTrue();
        workspaceManager.Verify(w => w.TryCleanupWorkspace(It.IsAny<string>()), Times.Once);
    }

    private static DefaultSubAgentManager CreateManager(
        IAgentSupervisor supervisor,
        IAgentRegistry registry,
        GatewayOptions? options = null,
        IAgentWorkspaceManager? workspaceManager = null)
        => new(
            supervisor,
            registry,
            new Mock<IActivityBroadcaster>().Object,
            new Mock<IChannelDispatcher>().Object,
            new TestOptionsMonitor<GatewayOptions>(options ?? new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance,
            workspaceManager);

    private static SubAgentSpawnRequest CreateSpawnRequest(int timeoutSeconds = 600)
        => new()
        {
            ParentAgentId = BotNexus.Domain.Primitives.AgentId.From("parent-agent"),
            ParentSessionId = BotNexus.Domain.Primitives.SessionId.From("parent-session"),
            Task = "Investigate flaky test",
            TimeoutSeconds = timeoutSeconds,
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conv")
        };

    private static Mock<IAgentRegistry> CreateRegistry()
    {
        var descriptor = CreateDescriptor();
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From("parent-agent"))).Returns(descriptor);
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
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("child-session"));
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new AgentResponse { Content = "never" };
            });
        return handle;
    }

    private static Mock<IAgentHandle> CreateFailingHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("child-session"));
        handle.SetupGet(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        return handle;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return;

            await Task.Delay(25);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

    private static InProcessIsolationStrategy CreateStrategy(
        IConversationStore? conversationStore = null,
        ISessionStore? sessionStore = null)
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
        if (conversationStore is not null)
            services.AddSingleton<IConversationStore>(conversationStore);
        if (sessionStore is not null)
            services.AddSingleton<ISessionStore>(sessionStore);

        return new InProcessIsolationStrategy(
            new LlmClient(new ApiProviderRegistry(), modelRegistry),
            new GatewayAuthManager(new StaticOptionsMonitor<PlatformConfig>(new PlatformConfig()), NullLogger<GatewayAuthManager>.Instance, new FileSystem()),
            new PassthroughContextBuilder(),
            new EmptyToolFactory(),
            new TestWorkspaceManager(),
            new DefaultToolRegistry([]),
            Array.Empty<IAgentToolContributor>(),
            new StubMemoryStoreFactory(),
            services.BuildServiceProvider(),
            NullLogger<InProcessIsolationStrategy>.Instance);
    }

    private static async Task SeedBoundSessionAsync(
        IConversationStore conversationStore,
        ISessionStore sessionStore,
        AgentId agentId,
        SessionId sessionId,
        ConversationId conversationId)
    {
        await conversationStore.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = agentId,
            ActiveSessionId = sessionId
        });
        var session = await sessionStore.GetOrCreateAsync(sessionId, agentId);
        session.Session.ConversationId = conversationId;
        await sessionStore.SaveAsync(session);
    }

    private static IReadOnlyList<string> GetToolNames(IAgentHandle handle)
    {
        var agentField = handle.GetType().GetField("_agent", BindingFlags.Instance | BindingFlags.NonPublic);
        agentField.ShouldNotBeNull();

        var agent = agentField!.GetValue(handle) as BotNexus.Agent.Core.Agent;
        agent.ShouldNotBeNull();

        return [.. agent!.State.Tools.Select(tool => tool.Name)];
    }

    private sealed class PassthroughContextBuilder : IContextBuilder
    {
        public Task<string> BuildSystemPromptAsync(
            AgentDescriptor descriptor,
            AgentExecutionContext? executionContext,
            CancellationToken ct = default)
            => Task.FromResult(descriptor.SystemPrompt ?? string.Empty);
    }

    private sealed class EmptyToolFactory : IAgentToolFactory
    {
        public IReadOnlyList<IAgentTool> CreateTools(string workingDirectory, IPathValidator? pathValidator = null, string[]? shellCommand = null) => [];
    }

    private sealed class TestWorkspaceManager : IAgentWorkspaceManager
    {
        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentWorkspace(agentName, string.Empty, string.Empty, string.Empty, string.Empty));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(
            string agentName,
            string? filePath,
            string content,
            string? memoryPathOverride,
            CancellationToken cancellationToken = default)
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

file sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
