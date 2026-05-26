using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
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
using BotNexus.Agent.Core;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Behavioural tests for the Phase 5 / F-6 (part 1) contract:
/// <list type="bullet">
///   <item><c>DefaultSubAgentManager.SpawnAsync</c> registers child descriptors with
///         <c>Kind = AgentKind.SubAgent</c>.</item>
///   <item>Parent / mirror-target descriptors are NEVER mutated to SubAgent —
///         they remain <c>AgentKind.Named</c>.</item>
///   <item><c>InProcessIsolationStrategy</c> blocks <c>spawn_subagent</c> tools when
///         <c>descriptor.Kind == AgentKind.SubAgent</c> (the new primary signal).</item>
///   <item>Defense-in-depth: <c>InProcessIsolationStrategy</c> also still honours the
///         legacy <c>SessionId.IsSubAgent</c> substring as a SECONDARY signal so the
///         gate fails CLOSED if a future path skips <c>DefaultSubAgentManager</c>.</item>
/// </list>
/// </summary>
public sealed class SubAgentKindTests
{
    [Fact]
    public async Task SpawnAsync_RegistersChildDescriptor_WithKindSubAgent()
    {
        var (registry, manager) = CreateRegistryAndManager();

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        var childAgentId = ResolveChildAgentId(registry, spawned.SubAgentId);
        var childDescriptor = registry.Get(childAgentId);
        childDescriptor.ShouldNotBeNull();
        childDescriptor!.Kind.ShouldBe(AgentKind.SubAgent,
            "DefaultSubAgentManager.SpawnAsync must register the child with Kind = SubAgent " +
            "so InProcessIsolationStrategy can deny recursive spawn tools using the typed " +
            "descriptor.Kind signal instead of substring-matching the SessionId.");
    }

    [Fact]
    public async Task SpawnAsync_DoesNotMutateParentDescriptor_KindRemainsNamed()
    {
        var (registry, manager) = CreateRegistryAndManager();
        var parentBefore = registry.Get(AgentId.From("parent-agent"));
        parentBefore.ShouldNotBeNull();
        parentBefore!.Kind.ShouldBe(AgentKind.Named, "Sanity: seeded parent must default to Named.");

        await manager.SpawnAsync(CreateSpawnRequest());

        var parentAfter = registry.Get(AgentId.From("parent-agent"));
        parentAfter.ShouldNotBeNull();
        parentAfter!.Kind.ShouldBe(AgentKind.Named,
            "SpawnAsync must NEVER mutate the parent descriptor's Kind. A named agent must " +
            "remain Named after spawning a sub-agent or it would lose access to spawn_subagent " +
            "on subsequent turns.");
    }

    [Fact]
    public async Task SpawnAsync_MirrorOfNamedAgent_ChildIsKindSubAgent_BaseRemainsNamed()
    {
        var (registry, manager) = CreateRegistryAndManager();

        // Register a named "researcher-prime" agent that the spawn will MIRROR.
        // The child should be Kind = SubAgent; the base researcher-prime must remain Named.
        var researcherPrime = new AgentDescriptor
        {
            AgentId = AgentId.From("researcher-prime"),
            DisplayName = "Researcher Prime",
            ModelId = "test-model",
            ApiProvider = "test-provider"
        };
        registry.Register(researcherPrime);

        var spawned = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Investigate via mirror of researcher-prime",
            TimeoutSeconds = 600,
            Mode = new Mirror(AgentId.From("researcher-prime")),
            InheritedConversationId = ConversationId.From("inherited-conv")
        });

        var childAgentId = ResolveChildAgentId(registry, spawned.SubAgentId);
        var childDescriptor = registry.Get(childAgentId);
        childDescriptor.ShouldNotBeNull();
        childDescriptor!.Kind.ShouldBe(AgentKind.SubAgent,
            "Mirror-of-named child must still be Kind = SubAgent. The spawned child is a " +
            "runtime ephemeral agent regardless of which descriptor it mirrors.");

        var basePrime = registry.Get(AgentId.From("researcher-prime"));
        basePrime.ShouldNotBeNull();
        basePrime!.Kind.ShouldBe(AgentKind.Named,
            "The mirrored TARGET descriptor must remain Named — it's a first-class registered " +
            "agent, not a sub-agent. Confusing the two would cause the named researcher-prime " +
            "to lose spawn_subagent on subsequent independent invocations.");
    }

    [Fact]
    public async Task IsolationStrategy_DescriptorKindSubAgent_PlainSessionId_BlocksSpawnTools()
    {
        // Primary signal under test: descriptor.Kind == SubAgent (no ::subagent:: in SessionId).
        // Pre-Phase-5 logic was substring-only and would have INCORRECTLY granted spawn tools
        // because the SessionId is plain. This test pins the new typed-routing primary signal.
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        await SeedBoundSessionAsync(conversationStore, sessionStore,
            AgentId.From("sub-agent"),
            SessionId.From("plain-session-no-substring"),
            ConversationId.From("inherited-conv"));

        var strategy = CreateStrategy(conversationStore, sessionStore);
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("sub-agent"),
            DisplayName = "Sub Agent",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ToolIds = ["spawn_subagent", "list_subagents", "manage_subagent"],
            Kind = AgentKind.SubAgent
        };

        var handle = await strategy.CreateAsync(descriptor, new AgentExecutionContext
        {
            SessionId = SessionId.From("plain-session-no-substring")
        });
        var toolNames = GetToolNames(handle);

        toolNames.ShouldNotContain("spawn_subagent",
            "PRIMARY SIGNAL: descriptor.Kind == SubAgent must block spawn_subagent registration " +
            "regardless of SessionId shape. If this fails, the typed-routing primary signal is " +
            "not wired into InProcessIsolationStrategy and we still have the F-11-shaped substring " +
            "dependency.");
        toolNames.ShouldNotContain("list_subagents");
        toolNames.ShouldNotContain("manage_subagent");
    }

    [Fact]
    public async Task IsolationStrategy_DescriptorKindNamed_SubAgentSubstringSessionId_StillBlocksSpawnTools()
    {
        // DEFENSE-IN-DEPTH signal under test: even if descriptor.Kind defaults to Named (e.g.,
        // a legacy path registered the sub-agent descriptor without Kind), the SessionId
        // ::subagent:: substring still triggers the block. The OR-gate fails CLOSED.
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        await SeedBoundSessionAsync(conversationStore, sessionStore,
            AgentId.From("legacy-agent"),
            SessionId.From("parent-session::subagent::legacy-child"),
            ConversationId.From("inherited-conv"));

        var strategy = CreateStrategy(conversationStore, sessionStore);
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("legacy-agent"),
            DisplayName = "Legacy Agent (Kind defaulted to Named — simulating bypass of SpawnAsync)",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ToolIds = ["spawn_subagent", "list_subagents", "manage_subagent"]
            // Kind deliberately NOT set — defaults to Named
        };

        var handle = await strategy.CreateAsync(descriptor, new AgentExecutionContext
        {
            SessionId = SessionId.From("parent-session::subagent::legacy-child")
        });
        var toolNames = GetToolNames(handle);

        toolNames.ShouldNotContain("spawn_subagent",
            "DEFENSE-IN-DEPTH: even with descriptor.Kind = Named (e.g., a future path that " +
            "registers a sub-agent descriptor without going through DefaultSubAgentManager), " +
            "the SessionId ::subagent:: substring must still cause the gate to fail CLOSED. " +
            "If this fails, we have regressed the security property to an OPEN-by-default gate.");
        toolNames.ShouldNotContain("list_subagents");
        toolNames.ShouldNotContain("manage_subagent");
    }

    [Fact]
    public async Task IsolationStrategy_DescriptorKindNamed_PlainSessionId_GrantsSpawnTools()
    {
        // Sanity baseline: a normal named agent with a normal session GETS the spawn tools.
        // This is the existing pre-Phase-5 behaviour; the test pins that the new Kind-based
        // gate did not regress the happy path.
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        await SeedBoundSessionAsync(conversationStore, sessionStore,
            AgentId.From("named-agent"),
            SessionId.From("ordinary-session"),
            ConversationId.From("inherited-conv"));

        var strategy = CreateStrategy(conversationStore, sessionStore);
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("named-agent"),
            DisplayName = "Named Agent",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ToolIds = ["spawn_subagent", "list_subagents", "manage_subagent"]
            // Kind defaults to Named.
        };

        var handle = await strategy.CreateAsync(descriptor, new AgentExecutionContext
        {
            SessionId = SessionId.From("ordinary-session")
        });
        var toolNames = GetToolNames(handle);

        toolNames.ShouldContain("spawn_subagent",
            "Sanity: named agent + ordinary session must STILL register spawn_subagent. " +
            "If this fails, the new Kind-based gate is over-broad and would block legitimate " +
            "named-agent spawning.");
        toolNames.ShouldContain("list_subagents");
        toolNames.ShouldContain("manage_subagent");
    }

    [Fact]
    public async Task IsolationStrategy_DescriptorKindSubAgent_SubAgentSubstringSessionId_BlocksSpawnTools()
    {
        // 4TH ROW OF THE OR-GATE TRUTH TABLE: both signals are TRUE (Kind = SubAgent AND
        // the SessionId carries the ::subagent:: substring). This is the normal-operation
        // shape — DefaultSubAgentManager.SpawnAsync sets BOTH signals together. Logically
        // redundant for an OR-gate, but the row exists as a regression-pin: if a future
        // refactor changes the gate to AND (which would fail OPEN under the previous three
        // rows for various single-signal cases), this row plus the prior two would still
        // block — but the prior two would START PASSING (i.e. NOT blocking). The full
        // 4-row matrix is the only configuration that lets a polarity-flip regression
        // surface in CI.
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        await SeedBoundSessionAsync(conversationStore, sessionStore,
            AgentId.From("sub-agent"),
            SessionId.From("parent::subagent::child"),
            ConversationId.From("inherited-conv"));

        var strategy = CreateStrategy(conversationStore, sessionStore);
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("sub-agent"),
            DisplayName = "Sub Agent (normal operation — both signals agree)",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ToolIds = ["spawn_subagent", "list_subagents", "manage_subagent"],
            Kind = AgentKind.SubAgent
        };

        var handle = await strategy.CreateAsync(descriptor, new AgentExecutionContext
        {
            SessionId = SessionId.From("parent::subagent::child")
        });
        var toolNames = GetToolNames(handle);

        toolNames.ShouldNotContain("spawn_subagent",
            "NORMAL OPERATION: both Kind and SessionId signals say SubAgent — gate must block. " +
            "This row plus the three single-signal rows form the complete 4-row OR-gate truth " +
            "table; a polarity flip from OR to AND would invert the three single-signal rows " +
            "but leave this row unchanged.");
        toolNames.ShouldNotContain("list_subagents");
        toolNames.ShouldNotContain("manage_subagent");
    }

    // ---------------------------------------------------------------------------
    // Test infrastructure (kept local to this file to avoid cross-test coupling).
    // ---------------------------------------------------------------------------

    private static (DefaultAgentRegistry Registry, DefaultSubAgentManager Manager) CreateRegistryAndManager()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var parent = new AgentDescriptor
        {
            AgentId = AgentId.From("parent-agent"),
            DisplayName = "Parent Agent",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ToolIds = ["spawn_subagent", "list_subagents", "manage_subagent"]
        };
        registry.Register(parent);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateHangingHandle().Object);

        var manager = new DefaultSubAgentManager(
            supervisor.Object,
            registry,
            new Mock<IActivityBroadcaster>().Object,
            new Mock<IChannelDispatcher>().Object,
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance);

        return (registry, manager);
    }

    private static AgentId ResolveChildAgentId(IAgentRegistry registry, string subAgentId)
    {
        var all = registry.GetAll().ToList();
        var match = all.FirstOrDefault(d => d.AgentId.Value.Contains(subAgentId, StringComparison.OrdinalIgnoreCase));
        match.ShouldNotBeNull(
            $"Expected a child descriptor whose AgentId contains the sub-agent id '{subAgentId}'. " +
            $"Registered: {string.Join(", ", all.Select(d => d.AgentId.Value))}");
        return match!.AgentId;
    }

    private static SubAgentSpawnRequest CreateSpawnRequest()
        => new()
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Investigate flaky test",
            TimeoutSeconds = 600,
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conv")
        };

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

    private static InProcessIsolationStrategy CreateStrategy(
        IConversationStore conversationStore,
        ISessionStore sessionStore)
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
            SubAgents = new SubAgentOptions { MaxDepth = 1 }
        }));
        services.AddSingleton(conversationStore);
        services.AddSingleton(sessionStore);

        return new InProcessIsolationStrategy(
            new LlmClient(new ApiProviderRegistry(), modelRegistry),
            new GatewayAuthManager(
                new StaticOptionsMonitor<PlatformConfig>(new PlatformConfig()),
                NullLogger<GatewayAuthManager>.Instance,
                new FileSystem()),
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
        var agentField = handle.GetType().GetField("_agent",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        agentField.ShouldNotBeNull();
        var agent = agentField!.GetValue(handle) as BotNexus.Agent.Core.Agent;
        agent.ShouldNotBeNull();
        return [.. agent!.State.Tools.Select(tool => tool.Name)];
    }
}

// File-scoped helpers (mirrors of the same private types inside SubAgentIntegrationTests).
// Local copies kept here so this test file is self-contained and does not depend on
// the private nested types of another test class. File-scoped means they don't leak
// into the rest of the assembly.

file sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

file sealed class PassthroughContextBuilder : IContextBuilder
{
    public Task<string> BuildSystemPromptAsync(
        AgentDescriptor descriptor,
        AgentExecutionContext? executionContext,
        CancellationToken ct = default)
        => Task.FromResult(descriptor.SystemPrompt ?? string.Empty);
}

file sealed class EmptyToolFactory : IAgentToolFactory
{
    public IReadOnlyList<IAgentTool> CreateTools(string workingDirectory, IPathValidator? pathValidator = null) => [];
}

file sealed class TestWorkspaceManager : IAgentWorkspaceManager
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

    public string GetWorkspacePath(string agentName) => AppContext.BaseDirectory;
}

file sealed class StubMemoryStoreFactory : IMemoryStoreFactory
{
    private readonly IMemoryStore _store = new StubMemoryStore();
    public IMemoryStore Create(string agentId) => _store;
}

file sealed class StubMemoryStore : IMemoryStore
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
