using BotNexus.Memory.Embeddings;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Tests.TestInfrastructure;

namespace BotNexus.Gateway.Tests.Isolation;

/// <summary>
/// Cross-seam coverage for issue #2796: a conversation-level model / thinking / context-window
/// override that is persisted on the bound conversation must be the value the injected runtime
/// block reports, not the agent descriptor default.
/// </summary>
/// <remarks>
/// These tests deliberately drive the <em>real</em> <see cref="WorkspaceContextBuilder"/> through
/// the <em>real</em> <see cref="InProcessIsolationStrategy"/> and assert on the rendered prompt
/// text carried by <see cref="InProcessAgentHandle.RenderedSystemPrompt"/>. Issue #2796 AC5 is
/// explicit that a test exercising only <c>ModelOverrideResolver</c> is insufficient: the defect
/// lived entirely in the seam between the resolver's output and prompt construction, so the
/// assertion has to start at a persisted conversation override and end at rendered text.
/// </remarks>
public sealed class EffectiveRuntimeContextTests
{
    private const string AgentDefaultModel = "claude-opus-5";
    private const string ConversationOverrideModel = "gpt-5.6-sol";

    [Fact]
    public async Task CreateAsync_WithConversationModelOverride_RuntimeBlockReportsOverrideModel()
    {
        var (strategy, sessionId) = await CreateSeamAsync(
            modelOverride: ConversationOverrideModel,
            thinkingOverride: null,
            contextWindowOverride: null);

        var prompt = await RenderPromptAsync(strategy, sessionId);

        // Assert on the delimiter-bounded runtime field, not a bare substring: the runtime line
        // also carries "default_model=claude-opus-5", which trivially CONTAINS "model=claude-opus-5".
        // A bare-substring negative assertion could therefore never hold and would prove nothing.
        prompt.ShouldContain($"| model={ConversationOverrideModel}");
        prompt.ShouldNotContain($"| model={AgentDefaultModel}");
    }

    [Fact]
    public async Task CreateAsync_WithConversationModelOverride_RuntimeBlockLabelsDescriptorModelAsDefault()
    {
        var (strategy, sessionId) = await CreateSeamAsync(
            modelOverride: ConversationOverrideModel,
            thinkingOverride: null,
            contextWindowOverride: null);

        var prompt = await RenderPromptAsync(strategy, sessionId);

        // AC1: the descriptor's configured model survives only as an explicitly labelled default.
        prompt.ShouldContain($"default_model={AgentDefaultModel}");
    }

    [Fact]
    public async Task CreateAsync_WithConversationThinkingOverride_RuntimeBlockReportsEffectiveReasoning()
    {
        var (strategy, sessionId) = await CreateSeamAsync(
            modelOverride: null,
            thinkingOverride: "high",
            contextWindowOverride: null);

        var prompt = await RenderPromptAsync(strategy, sessionId);

        prompt.ShouldContain("Reasoning: thinking level high");
        // #2874: "off" was a fabricated display mode; an unresolved level now omits the line.
        prompt.ShouldNotContain("Reasoning: thinking level off");
    }

    [Fact]
    public async Task CreateAsync_WithConversationContextWindowOverride_RuntimeBlockReportsEffectiveContextWindow()
    {
        var (strategy, sessionId) = await CreateSeamAsync(
            modelOverride: null,
            thinkingOverride: null,
            contextWindowOverride: 321_000);

        var prompt = await RenderPromptAsync(strategy, sessionId);

        prompt.ShouldContain("context_window=321000");
    }

    [Fact]
    public async Task CreateAsync_WithConversationOverrides_RuntimeBlockMatchesAgentOptions()
    {
        // AC2: the runtime surface and AgentOptions must not drift - they are asserted against
        // each other from a single CreateAsync call, not against two independently expected values.
        var (strategy, sessionId) = await CreateSeamAsync(
            modelOverride: ConversationOverrideModel,
            thinkingOverride: "medium",
            contextWindowOverride: 250_000);

        var handle = await strategy.CreateAsync(CreateDescriptor(), new AgentExecutionContext { SessionId = sessionId });
        var inProcessHandle = handle.ShouldBeOfType<InProcessAgentHandle>();
        var settings = GetGenerationSettings(handle);

        settings.Reasoning.ShouldBe(ThinkingLevel.Medium);
        settings.ContextWindow.ShouldBe(250_000);

        var prompt = inProcessHandle.RenderedSystemPrompt.ShouldNotBeNull();
        prompt.ShouldContain("Reasoning: thinking level medium");
        prompt.ShouldContain($"context_window={settings.ContextWindow}");
        prompt.ShouldContain($"| model={ConversationOverrideModel}");
    }

    [Fact]
    public async Task CreateAsync_WithoutConversationOverride_RuntimeBlockReportsAgentDefaults()
    {
        // AC3: with no conversation override the runtime block is unchanged - the agent model is
        // reported as the effective model and no stale default_model duplicate is emitted.
        var (strategy, sessionId) = await CreateSeamAsync(
            modelOverride: null,
            thinkingOverride: null,
            contextWindowOverride: null);

        var prompt = await RenderPromptAsync(strategy, sessionId);

        prompt.ShouldContain($"| model={AgentDefaultModel}");
        prompt.ShouldNotContain("default_model=");
        // #2874: with no thinking level resolved the runtime block omits the reasoning subject
        // entirely rather than reporting a nonexistent "off" display mode.
        prompt.ShouldNotContain("Reasoning:");
    }

    // ─── Seam construction ────────────────────────────────────────────────

    private static async Task<string> RenderPromptAsync(InProcessIsolationStrategy strategy, SessionId sessionId)
    {
        var handle = await strategy.CreateAsync(CreateDescriptor(), new AgentExecutionContext { SessionId = sessionId });
        return handle.ShouldBeOfType<InProcessAgentHandle>().RenderedSystemPrompt.ShouldNotBeNull();
    }

    private static SimpleStreamOptions GetGenerationSettings(IAgentHandle handle)
    {
        var agentField = handle.GetType().GetField("_agent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        agentField.ShouldNotBeNull();
        var agent = agentField.GetValue(handle) as BotNexus.Agent.Core.Agent;
        agent.ShouldNotBeNull();
        var optionsField = typeof(BotNexus.Agent.Core.Agent).GetField("_options", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        optionsField.ShouldNotBeNull();
        var options = optionsField.GetValue(agent).ShouldBeOfType<BotNexus.Agent.Core.Configuration.AgentOptions>();
        return options.GenerationSettings.ShouldBeOfType<SimpleStreamOptions>();
    }

    private static async Task<(InProcessIsolationStrategy Strategy, SessionId SessionId)> CreateSeamAsync(
        string? modelOverride,
        string? thinkingOverride,
        int? contextWindowOverride)
    {
        var agentId = AgentId.From("agent-2796");
        var sessionId = SessionId.From("session-2796");
        var conversationId = ConversationId.From("c_2796");

        var conversationStore = new InMemoryConversationStore();
        await conversationStore.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = agentId,
            ActiveSessionId = sessionId,
            ModelOverride = modelOverride,
            ThinkingOverride = thinkingOverride,
            ContextWindowOverride = contextWindowOverride
        });

        var sessionStore = new InMemorySessionStore();
        await sessionStore.SaveAsync(new GatewaySession
        {
            SessionId = sessionId,
            AgentId = agentId,
            ConversationId = conversationId
        });

        var services = new ServiceCollection();
        services.AddSingleton<IConversationStore>(conversationStore);
        services.AddSingleton<ISessionStore>(sessionStore);
        var provider = services.BuildServiceProvider();

        var modelRegistry = new ModelRegistry();
        modelRegistry.Register("github-copilot", CreateModel(AgentDefaultModel));
        modelRegistry.Register("github-copilot", CreateModel(ConversationOverrideModel));
        var llmClient = new LlmClient(new ApiProviderRegistry(), modelRegistry);

        var workspacePath = Path.Combine(Path.GetTempPath(), "botnexus-2796", Guid.NewGuid().ToString("N"));
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(workspacePath);

        var contextBuilder = new WorkspaceContextBuilder(
            new FixedWorkspaceManager(workspacePath),
            fileSystem,
            conversationStore,
            sessionStore);

        var strategy = new InProcessIsolationStrategy(
            llmClient,
            new GatewayAuthManager(
                new FixedOptionsMonitor<PlatformConfig>(new PlatformConfig()),
                NullLogger<GatewayAuthManager>.Instance,
                new FileSystem()),
            contextBuilder,
            new NoToolsFactory(),
            new FixedWorkspaceManager(workspacePath),
            new DefaultToolRegistry(Array.Empty<IAgentTool>()),
            Array.Empty<IAgentToolContributor>(),
            new NoOpMemoryStoreFactory(),
            new StubAgentMemoryFactory(),
            provider,
            NullLogger<InProcessIsolationStrategy>.Instance);

        return (strategy, sessionId);
    }

    private static LlmModel CreateModel(string id) => new(
        Id: id,
        Name: id,
        Api: "test-api",
        Provider: "github-copilot",
        BaseUrl: "http://localhost",
        Reasoning: true,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 200_000,
        MaxTokens: 64_000,
        SupportsExtraHighThinking: true,
        SupportsExtendedContextWindow: true);

    private static AgentDescriptor CreateDescriptor() => new()
    {
        AgentId = AgentId.From("agent-2796"),
        DisplayName = "Agent 2796",
        ModelId = AgentDefaultModel,
        ApiProvider = "github-copilot",
        IsolationStrategy = "in-process",
        SystemPrompt = "base prompt",
        ToolIds = []
    };

    // ─── Fakes ────────────────────────────────────────────────────────────

    private sealed class FixedWorkspaceManager(string workspacePath) : IAgentWorkspaceManager
    {
        public string GetWorkspacePath(string agentName) => workspacePath;

        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentWorkspace(agentName, null, null, null, null));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, string? memoryPathOverride, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoToolsFactory : IAgentToolFactory
    {
        public IReadOnlyList<IAgentTool> CreateTools(WorkingDir workingDirectory, IPathValidator? pathValidator = null, string[]? shellCommand = null)
            => Array.Empty<IAgentTool>();
    }

    private sealed class NoOpMemoryStoreFactory : IMemoryStoreFactory
    {
        public IMemoryStore Create(AgentId agentId) => new NoOpMemoryStore();
    }

    private sealed class NoOpMemoryStore : IMemoryStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<MemoryEntry> InsertAsync(MemoryEntry entry, CancellationToken ct = default) => Task.FromResult(entry);
        public Task<MemoryEntry?> GetByIdAsync(string id, CancellationToken ct = default) => Task.FromResult<MemoryEntry?>(null);
        public Task<IReadOnlyList<MemoryEntry>> GetBySessionAsync(string sessionId, int limit = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

        // #2781: explicit pass-through. Required (not default-implemented) on IMemoryStore because
        // Moq returns null for default interface methods rather than running the default body.
        public async Task<IReadOnlyList<ScoredMemoryEntry>> SearchScoredAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
        {
            var entries = await SearchAsync(query, topK, filter, ct);
            return entries.Select(entry => new ScoredMemoryEntry(entry, 0d)).ToList();
        }

        // #3244: explicit pass-through with a NotAttempted scan report - this stub runs no bounded
        // vector scan, so claiming any coverage would be a lie the caller could act on.
        public async Task<MemorySearchResult> SearchWithReportAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
            => new(await SearchScoredAsync(query, topK, filter, ct), MemoryVectorScanReport.NotAttempted);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<MemoryStoreStats> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(new MemoryStoreStats(0, 0, null));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

file sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
