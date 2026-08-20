using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Gateway.Security;
using BotNexus.Memory;
using BotNexus.Memory.Embeddings;
using BotNexus.Memory.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NSubstitute;
using Shouldly;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Always-on memory injection must be gated on the turn's effective tool policy, not on note
/// provenance alone (#3468).
/// </summary>
/// <remarks>
/// <para>
/// These tests deliberately drive the <b>real</b> spawn path -
/// <see cref="DefaultSubAgentManager.SpawnAsync"/> with a restricted archetype - and then the
/// <b>real</b> <see cref="WorkspaceContextBuilder"/>, <see cref="DefaultToolPolicyProvider"/> and
/// <see cref="MarkdownAgentMemory"/> over an in-memory file system. Constructing
/// <c>MemoryInjectionGate</c> directly would prove the gate has a parameter and nothing at all
/// about whether the seam is wired: the defect in #3468 was precisely that every layer between the
/// policy and the gate dropped the signal on the floor.
/// </para>
/// <para>
/// The note content is first-party, so the pre-existing provenance filter admits all of it. Any
/// exclusion observed here is therefore attributable to the capability axis alone.
/// </para>
/// </remarks>
public sealed class MemoryInjectionToolPolicyTests
{
    private const string DailyNoteContent = "PRIVATE DAILY NOTE FOR THE PARENT AGENT";

    [Fact]
    public async Task SubAgentSpawnedWithAnArchetypeThatExcludesMemoryTools_GetsNoInjectedDailyNotes()
    {
        // AC2 + AC4. `coder` grants read/write/shell and no memory tool at all
        // (BuiltInArchetypes), so the spawned child is exactly the "deliberately scoped without
        // memory" case the issue describes.
        var (registry, manager, fileSystem, workspaceManager) = CreateHarness();

        var childDescriptor = await SpawnAndResolveChildAsync(registry, manager, SubAgentArchetype.Coder);

        childDescriptor.ToolIds.Contains("memory_search").ShouldBeFalse(
            "Sanity: the `coder` archetype must not grant memory tools, or this test proves nothing.");

        var prompt = await BuildPromptAsync(childDescriptor, fileSystem, workspaceManager);

        prompt.Contains(DailyNoteContent, StringComparison.Ordinal).ShouldBeFalse(
            "A sub-agent whose archetype excludes memory tools must receive NO injected daily-note " +
            "content. Injecting it anyway pushes the parent's private memory across an agent " +
            "boundary that was drawn on purpose (#3468 clause 2).");
    }

    [Fact]
    public async Task SubAgentSpawnedWithAnArchetypeThatGrantsMemorySearch_StillGetsItsDailyNotes()
    {
        // The happy path, and the reason the assertion above is not vacuous. `researcher` grants
        // memory_search, so the identical harness must still inject. Without this row the fix
        // could "pass" by disabling memory injection for every sub-agent.
        var (registry, manager, fileSystem, workspaceManager) = CreateHarness();

        var childDescriptor = await SpawnAndResolveChildAsync(registry, manager, SubAgentArchetype.Researcher);

        childDescriptor.ToolIds.Contains("memory_search").ShouldBeTrue("Sanity: `researcher` grants memory_search.");

        var prompt = await BuildPromptAsync(childDescriptor, fileSystem, workspaceManager);

        prompt.Contains(DailyNoteContent, StringComparison.Ordinal).ShouldBeTrue(
            "An agent that CAN call memory tools must keep its always-on daily-note injection. " +
            "The capability gate narrows injection; it must not disable it.");
    }

    [Fact]
    public async Task AgentWithAnExplicitToolIdsListExcludingMemory_GetsNoInjectedDailyNotes()
    {
        // AC4's second named path: an explicit `toolIds` grant rather than an archetype. Same
        // seam, different way of arriving at a restricted descriptor.
        var (_, _, fileSystem, workspaceManager) = CreateHarness();

        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("tool-restricted-agent"),
            DisplayName = "Tool Restricted",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ToolIds = ["read", "glob", "grep"]
        };

        var prompt = await BuildPromptAsync(descriptor, fileSystem, workspaceManager);

        prompt.Contains(DailyNoteContent, StringComparison.Ordinal).ShouldBeFalse(
            "An explicit toolIds allowlist without any memory tool denies memory just as an " +
            "archetype restriction does; both resolve through the same policy seam (#3468).");
    }

    [Fact]
    public async Task AgentWithAnUnrestrictedToolSet_StillGetsItsDailyNotes()
    {
        // The ordinary named-agent case: no allowlist at all means every tool, so nothing changes.
        var (_, _, fileSystem, workspaceManager) = CreateHarness();

        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("unrestricted-agent"),
            DisplayName = "Unrestricted",
            ModelId = "test-model",
            ApiProvider = "test-provider"
        };

        var prompt = await BuildPromptAsync(descriptor, fileSystem, workspaceManager);

        prompt.Contains(DailyNoteContent, StringComparison.Ordinal).ShouldBeTrue(
            "An agent with no tool restriction must be completely unaffected by #3468.");
    }

    // ---------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------

    private static (DefaultAgentRegistry Registry, DefaultSubAgentManager Manager, MockFileSystem FileSystem, StubWorkspaceManager WorkspaceManager)
        CreateHarness()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = AgentId.From("parent-agent"),
            DisplayName = "Parent Agent",
            ModelId = "test-model",
            ApiProvider = "test-provider"
        });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateHangingHandle());

        var manager = new DefaultSubAgentManager(
            supervisor.Object,
            registry,
            new Mock<IActivityBroadcaster>().Object,
            new Mock<IChannelDispatcher>().Object,
            new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance);

        var workspacePath = Path.Combine(Path.GetTempPath(), "bn-3468-" + Guid.NewGuid().ToString("N"), "workspace");
        var fileSystem = new MockFileSystem();
        fileSystem.Directory.CreateDirectory(Path.Combine(workspacePath, "memory"));
        fileSystem.File.WriteAllText(
            Path.Combine(workspacePath, "memory", $"{DateTime.Now:yyyy-MM-dd}.md"),
            DailyNoteContent);

        return (registry, manager, fileSystem, new StubWorkspaceManager(workspacePath));
    }

    private static async Task<AgentDescriptor> SpawnAndResolveChildAsync(
        DefaultAgentRegistry registry,
        DefaultSubAgentManager manager,
        SubAgentArchetype archetype)
    {
        var spawned = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "irrelevant to prompt assembly",
            TimeoutSeconds = 600,
            Mode = new Embody(archetype),
            InheritedConversationId = ConversationId.From("inherited-conv")
        });

        var child = registry.GetAll()
            .FirstOrDefault(d => d.AgentId.Value.Contains(spawned.SubAgentId, StringComparison.OrdinalIgnoreCase));

        child.ShouldNotBeNull(
            $"Expected a registered child descriptor for sub-agent '{spawned.SubAgentId}'. " +
            $"Registered: {string.Join(", ", registry.GetAll().Select(d => d.AgentId.Value))}");

        return child!;
    }

    /// <summary>
    /// Assembles the system prompt through the real builder, the real policy provider and the real
    /// markdown memory provider. Everything below the descriptor is production code.
    /// </summary>
    private static async Task<string> BuildPromptAsync(
        AgentDescriptor descriptor,
        MockFileSystem fileSystem,
        StubWorkspaceManager workspaceManager)
    {
        var homePath = Path.Combine(Path.GetTempPath(), "bn-3468-home-" + Guid.NewGuid().ToString("N"));
        fileSystem.Directory.CreateDirectory(homePath);

        var policyProvider = new DefaultToolPolicyProvider(
            new StaticOptionsMonitor<PlatformConfig>(new PlatformConfig()),
            NullLogger<DefaultToolPolicyProvider>.Instance);

        var builder = new WorkspaceContextBuilder(
            workspaceManager,
            fileSystem,
            new BotNexusHome(fileSystem, homePath),
            Substitute.For<IConversationStore>(),
            Substitute.For<ISessionStore>(),
            new MarkdownAgentMemoryFactory(workspaceManager, fileSystem),
            policyProvider);

        return await builder.BuildSystemPromptAsync(descriptor, executionContext: null);
    }

    private static IAgentHandle CreateHangingHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("child-session"));
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle
            .Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new AgentResponse { Content = "never" };
            });
        return handle.Object;
    }

    internal sealed class StubWorkspaceManager(string workspacePath) : IAgentWorkspaceManager
    {
        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentWorkspace(agentName, workspacePath, string.Empty, string.Empty, string.Empty));

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

        public string GetWorkspacePath(string agentName) => workspacePath;
    }
}

/// <summary>
/// Produces the REAL <see cref="MarkdownAgentMemory"/> over the test file system, so the injection
/// path under test is the production one rather than a stub that could not exhibit the defect.
/// </summary>
file sealed class MarkdownAgentMemoryFactory(IAgentWorkspaceManager workspaceManager, IFileSystem fileSystem)
    : IAgentMemoryFactory
{
    public IAgentMemory Create(string agentId, string? providerName = null)
        => new MarkdownAgentMemory(agentId, workspaceManager, new NoOpMemoryStore(), fileSystem);

    public IReadOnlyList<string> GetRegisteredProviders() => ["markdown"];
}

/// <summary>
/// The prompt-context path never touches the store; a no-op keeps SQLite out of the test.
/// </summary>
file sealed class NoOpMemoryStore : IMemoryStore
{
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<MemoryEntry> InsertAsync(MemoryEntry entry, CancellationToken ct = default) => Task.FromResult(entry);

    public Task<MemoryEntry?> GetByIdAsync(string id, CancellationToken ct = default) => Task.FromResult<MemoryEntry?>(null);

    public Task<IReadOnlyList<MemoryEntry>> GetBySessionAsync(string sessionId, int limit = 20, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

    public Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

    public async Task<IReadOnlyList<ScoredMemoryEntry>> SearchScoredAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
    {
        var entries = await SearchAsync(query, topK, filter, ct);
        return [.. entries.Select(entry => new ScoredMemoryEntry(entry, 0d))];
    }

    public async Task<MemorySearchResult> SearchWithReportAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
        => new(await SearchScoredAsync(query, topK, filter, ct), MemoryVectorScanReport.NotAttempted);

    public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<MemoryStoreStats> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(new MemoryStoreStats(0, 0, null));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

file sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
