using System.IO.Abstractions;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Isolation;
using BotNexus.Gateway.Security;
using BotNexus.Gateway.Tests.TestInfrastructure;
using BotNexus.Gateway.Tools;
using BotNexus.Memory;
using BotNexus.Memory.Embeddings;
using BotNexus.Memory.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Isolation;

/// <summary>
/// #3746 - <c>CreateAsync</c> logged its success return ("Created agent handle for ...") at
/// <see cref="LogLevel.Warning"/>. That is a routine per-session lifecycle event with no failure
/// semantics, and at fleet scale it accounted for 44% of every warning the gateway emitted,
/// which destroys the warning channel's value as a health signal.
///
/// The fence asserts the ABSENCE of a warning, so it must also prove the run was real - an
/// absence-assertion alone is trivially satisfied by a strategy that does nothing and logs
/// nothing. Each test therefore additionally asserts that the handle was created AND that at
/// least one non-Warning record was emitted.
/// </summary>
public sealed class InProcessIsolationStrategyHandleLogLevelTests
{
    [Fact]
    public async Task CreateAsync_OnSuccessPath_EmitsNoWarningRecord()
    {
        var logger = new CapturingLogger<InProcessIsolationStrategy>();
        var strategy = CreateStrategy(logger);

        var handle = await strategy.CreateAsync(
            CreateDescriptor(),
            new AgentExecutionContext { SessionId = SessionId.From("session-3746-1") });

        // Non-vacuity part 1: the success path actually ran to completion and produced a handle.
        // Without this, a strategy that threw before reaching any log statement would pass.
        handle.ShouldNotBeNull();

        var records = logger.Records;

        // Non-vacuity part 2: the strategy really did log during this run. Without this, a
        // strategy that emits nothing at all - or a logger fake that captures nothing - would
        // satisfy the warning-free assertion below for entirely the wrong reason.
        records.Any(r => r.Level != LogLevel.Warning).ShouldBeTrue(
            "the success path must still emit diagnostics at a non-Warning level; captured: "
            + Render(records));

        // The actual regression fence.
        records
            .Where(r => r.Level >= LogLevel.Warning)
            .ShouldBeEmpty("a successful CreateAsync must not emit Warning or above; captured: " + Render(records));
    }

    [Fact]
    public async Task CreateAsync_HandleCreationEvent_IsLoggedBelowWarning()
    {
        // Level justification: the caller already records this lifecycle event at Information
        // ("Created agent instance '{AgentId}' for session '{SessionId}' (isolation: in-process)").
        // What this line adds is the resolved tool roster - diagnostic detail, hence Debug.
        var logger = new CapturingLogger<InProcessIsolationStrategy>();
        var strategy = CreateStrategy(logger);

        var handle = await strategy.CreateAsync(
            CreateDescriptor(),
            new AgentExecutionContext { SessionId = SessionId.From("session-3746-2") });

        handle.ShouldNotBeNull();

        var record = logger.Records.FirstOrDefault(
            r => r.Message.Contains("Created agent handle", StringComparison.Ordinal));

        // Positive assertion: the event must still be observable somewhere. Silencing the line
        // entirely is not the fix and must not pass this test.
        record.ShouldNotBeNull(
            "the handle-creation event must still be logged, just not at Warning; captured: "
            + Render(logger.Records));
        record!.Level.ShouldBe(LogLevel.Debug);
        record.Message.ShouldContain("session-3746-2");
    }

    private static string Render(IReadOnlyList<CapturedLogRecord> records)
        => records.Count == 0
            ? "<no records captured>"
            : string.Join(" | ", records.Select(r => $"{r.Level}:{r.Message}"));

    private static InProcessIsolationStrategy CreateStrategy(ILogger<InProcessIsolationStrategy> logger)
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

        var llmClient = new LlmClient(new ApiProviderRegistry(), modelRegistry);

        return new InProcessIsolationStrategy(
            llmClient,
            new GatewayAuthManager(
                new LogLevelStaticOptionsMonitor<PlatformConfig>(new PlatformConfig()),
                NullLogger<GatewayAuthManager>.Instance,
                new FileSystem()),
            new LogLevelPassthroughContextBuilder(),
            new LogLevelAgentToolFactory(),
            new LogLevelWorkspaceManager(),
            new DefaultToolRegistry(Array.Empty<IAgentTool>()),
            Array.Empty<IAgentToolContributor>(),
            new LogLevelMemoryStoreFactory(),
            new StubAgentMemoryFactory(),
            new ServiceCollection().BuildServiceProvider(),
            logger);
    }

    private static AgentDescriptor CreateDescriptor()
        => new()
        {
            AgentId = AgentId.From("agent-3746"),
            DisplayName = "Agent 3746",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            SystemPrompt = "You are a test agent."
        };
}

file sealed class LogLevelStaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

file sealed class LogLevelPassthroughContextBuilder : IContextBuilder
{
    public Task<string> BuildSystemPromptAsync(
        AgentDescriptor descriptor,
        AgentExecutionContext? executionContext,
        EffectiveExecutionSettings? effectiveSettings = null,
        CancellationToken ct = default)
        => Task.FromResult(descriptor.SystemPrompt ?? string.Empty);
}

file sealed class LogLevelAgentToolFactory : IAgentToolFactory
{
    public IReadOnlyList<IAgentTool> CreateTools(
        WorkingDir workingDirectory,
        IPathValidator? pathValidator = null,
        string[]? shellCommand = null)
        => [];
}

file sealed class LogLevelWorkspaceManager : IAgentWorkspaceManager
{
    public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken cancellationToken = default)
        => Task.FromResult(new AgentWorkspace(agentName, string.Empty, string.Empty, string.Empty, string.Empty));

    public Task SaveMemoryAsync(string agentName, string content, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SaveMemoryAsync(string agentName, string? filePath, string content, string? memoryPathOverride, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public string GetWorkspacePath(string agentName) => Path.Combine(Path.GetTempPath(), "bn-3746", agentName);
}

file sealed class LogLevelMemoryStoreFactory : IMemoryStoreFactory
{
    private readonly IMemoryStore _store = new LogLevelMemoryStore();

    public IMemoryStore Create(AgentId agentId) => _store;
}

file sealed class LogLevelMemoryStore : IMemoryStore
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
        return entries.Select(entry => new ScoredMemoryEntry(entry, 0d)).ToList();
    }

    public async Task<MemorySearchResult> SearchWithReportAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
        => new(await SearchScoredAsync(query, topK, filter, ct), MemoryVectorScanReport.NotAttempted);

    public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<MemoryStoreStats> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(new MemoryStoreStats(0, 0, null));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
