using BotNexus.Domain.Primitives;
using BotNexus.Memory.Embeddings;
using System.IO.Abstractions;
using System.Reflection;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
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
using BotNexus.Memory.Models;
using BotNexus.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Isolation;

/// <summary>
/// #2548 - the agent core emits non-fatal runtime diagnostics through
/// <c>AgentOptions.OnDiagnostic</c>. Nothing in production assigned that callback, so every
/// diagnostic the core produced was silently discarded. These tests assert the OBSERVABLE:
/// a diagnostic produced inside <see cref="BotNexus.Agent.Core.Agent"/> is RECEIVED by the
/// host's <see cref="ILogger"/>. Asserting the delegate is merely non-null would not prove
/// the wiring reaches the host, so these tests drive a real producer end to end.
/// </summary>
public sealed class InProcessIsolationStrategyDiagnosticSinkTests
{
    [Fact]
    public async Task AgentCoreDiagnostic_WhenListenerThrows_IsReceivedByHostLogger()
    {
        var logger = new CapturingLogger<InProcessIsolationStrategy>();
        var strategy = CreateStrategy(logger);

        var handle = await strategy.CreateAsync(
            CreateDescriptor(),
            new AgentExecutionContext { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-diag-1") });

        var agent = GetAgent(handle);

        // A listener that throws is exactly the non-fatal condition the core reports through
        // OnDiagnostic. The core swallows the exception and hands the message to the sink.
        using var subscription = agent.Subscribe((_, _) => throw new InvalidOperationException("boom-2548"));

        // The LLM provider is not registered for this test model, so the run fails; the core
        // still emits its lifecycle events, and the throwing listener triggers the diagnostic.
        await agent.PromptAsync("hello");

        var diagnostics = logger.Records
            .Where(r => r.Level == LogLevel.Warning && r.Message.Contains("boom-2548", StringComparison.Ordinal))
            .ToList();

        diagnostics.ShouldNotBeEmpty(
            "an agent-core diagnostic must reach the host logger; received: "
            + string.Join(" | ", logger.Records.Select(r => $"{r.Level}:{r.Message}")));
    }

    [Fact]
    public async Task AgentCoreDiagnostic_WhenReceived_IsLoggedAtWarning()
    {
        // Severity justification: these are non-fatal conditions the agent swallowed to keep the
        // run alive (a listener threw, an agent_end notification failed). They are not errors that
        // failed the turn, but they are always unexpected and represent lost work, so Information
        // would bury them and Error would over-page. Warning is the correct level.
        var logger = new CapturingLogger<InProcessIsolationStrategy>();
        var strategy = CreateStrategy(logger);

        var handle = await strategy.CreateAsync(
            CreateDescriptor(),
            new AgentExecutionContext { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-diag-2") });

        var agent = GetAgent(handle);
        using var subscription = agent.Subscribe((_, _) => throw new InvalidOperationException("severity-2548"));

        await agent.PromptAsync("hello");

        var record = logger.Records.FirstOrDefault(r => r.Message.Contains("severity-2548", StringComparison.Ordinal));
        record.ShouldNotBeNull("the agent-core diagnostic never reached the host logger");
        record!.Level.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public async Task AgentCoreDiagnostic_WhenReceived_CarriesAgentAndSessionIdentity()
    {
        var logger = new CapturingLogger<InProcessIsolationStrategy>();
        var strategy = CreateStrategy(logger);

        var handle = await strategy.CreateAsync(
            CreateDescriptor(),
            new AgentExecutionContext { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-diag-3") });

        var agent = GetAgent(handle);
        using var subscription = agent.Subscribe((_, _) => throw new InvalidOperationException("identity-2548"));

        await agent.PromptAsync("hello");

        var record = logger.Records.FirstOrDefault(r => r.Message.Contains("identity-2548", StringComparison.Ordinal));
        record.ShouldNotBeNull("the agent-core diagnostic never reached the host logger");
        record!.Message.ShouldContain("agent-a");
        record.Message.ShouldContain("session-diag-3");
    }

    private static BotNexus.Agent.Core.Agent GetAgent(IAgentHandle handle)
    {
        var agentField = handle.GetType().GetField("_agent", BindingFlags.Instance | BindingFlags.NonPublic);
        agentField.ShouldNotBeNull();
        var agent = agentField!.GetValue(handle) as BotNexus.Agent.Core.Agent;
        agent.ShouldNotBeNull();
        return agent!;
    }

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
                new DiagnosticStaticOptionsMonitor<PlatformConfig>(new PlatformConfig()),
                NullLogger<GatewayAuthManager>.Instance,
                new FileSystem()),
            new DiagnosticPassthroughContextBuilder(),
            new DiagnosticAgentToolFactory(),
            new DiagnosticWorkspaceManager(),
            new DefaultToolRegistry(Array.Empty<IAgentTool>()),
            Array.Empty<IAgentToolContributor>(),
            new DiagnosticMemoryStoreFactory(),
            new StubAgentMemoryFactory(),
            new ServiceCollection().BuildServiceProvider(),
            logger);
    }

    private static AgentDescriptor CreateDescriptor()
        => new()
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            SystemPrompt = "You are a test agent."
        };

    private sealed class DiagnosticPassthroughContextBuilder : IContextBuilder
    {
        public Task<string> BuildSystemPromptAsync(
            AgentDescriptor descriptor,
            AgentExecutionContext? executionContext,
            EffectiveExecutionSettings? effectiveSettings = null,
            CancellationToken ct = default)
            => Task.FromResult(descriptor.SystemPrompt ?? string.Empty);
    }

    private sealed class DiagnosticAgentToolFactory : IAgentToolFactory
    {
        public IReadOnlyList<IAgentTool> CreateTools(WorkingDir workingDirectory, IPathValidator? pathValidator = null, string[]? shellCommand = null)
            => [];
    }

    private sealed class DiagnosticWorkspaceManager : IAgentWorkspaceManager
    {
        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentWorkspace(agentName, string.Empty, string.Empty, string.Empty, string.Empty));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, string? memoryPathOverride, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public string GetWorkspacePath(string agentName) => Path.Combine(Path.GetTempPath(), "bn-2548", agentName);
    }

    private sealed class DiagnosticMemoryStoreFactory : IMemoryStoreFactory
    {
        private readonly IMemoryStore _store = new DiagnosticMemoryStore();

        public IMemoryStore Create(AgentId agentId) => _store;
    }

    private sealed class DiagnosticMemoryStore : IMemoryStore
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

file sealed class DiagnosticStaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>
/// Captures every log record written by the component under test so a test can assert on the
/// rendered message and severity. Used by #2548 to prove agent-core diagnostics reach the host.
/// </summary>
internal sealed record CapturedLogRecord(LogLevel Level, string Message);

internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<CapturedLogRecord> _records = [];
    private readonly object _gate = new();

    public IReadOnlyList<CapturedLogRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _records.Add(new CapturedLogRecord(logLevel, formatter(state, exception)));
        }
    }
}
