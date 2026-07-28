using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// #2460: an aborted compaction used to log <c>outcome=Aborted</c> with NO reason, hiding a
/// repeating no-op abort loop (50 consecutive aborts observed in production with the preserved
/// count climbing monotonically). These tests pin the SPECIFIC reason code emitted for each
/// specific abort branch, plus the minimal loop guard.
/// </summary>
public sealed class CompactionAbortReasonTests
{
    private static readonly AgentId TestAgent = AgentId.From("test-agent");

    private static readonly LlmModel TestModel = new(
        Id: "test-model",
        Name: "Test Model",
        Api: "test-api",
        Provider: "test-provider",
        BaseUrl: "https://example.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 32000,
        MaxTokens: 4096);

    // ── Compactor: per-branch reason codes ───────────────────────────────────

    [Fact]
    public async Task CompactAsync_EmptyHistory_StampsEmptyHistoryReason()
    {
        var session = CreateSession();
        var compactor = CreateCompactor("unused");

        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 2,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeFalse();
        result.SkipReason.ShouldBe(CompactionSkipReasons.EmptyHistory);
    }

    [Fact]
    public async Task CompactAsync_NoSummarizableTurns_StampsNoSummarizableTurnsReason()
    {
        // Two user turns, PreservedTurns=3 => split finds nothing, and the tail is below the
        // threshold so the #1574 fallback does not engage. This is the observed loop branch.
        var session = CreateSession(
            ("user", "u1"),
            ("assistant", "a1"),
            ("user", "u2"),
            ("assistant", "a2"));
        var compactor = CreateCompactor("unused");

        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 3,
            ContextWindowTokens = 1_000_000,
            TokenThresholdRatio = 0.5,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeFalse();
        result.SkipReason.ShouldBe(CompactionSkipReasons.NoSummarizableTurns);
    }

    [Fact]
    public async Task CompactAsync_EmptySummary_StampsEmptySummaryReason()
    {
        var session = CreateSession(
            ("user", "u1"),
            ("assistant", "a1"),
            ("user", "u2"),
            ("assistant", "a2"),
            ("user", "u3"));
        var compactor = CreateCompactor(string.Empty);

        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeFalse();
        result.SkipReason.ShouldBe(CompactionSkipReasons.EmptySummary);
    }

    [Fact]
    public async Task CompactAsync_RepeatedNoSummarizableTurns_OpensCircuitBreakerLoopGuard()
    {
        // The loop guard: a split that summarizes nothing counts as a failure, so after
        // MaxConsecutiveFailures attempts the existing per-session breaker opens and the
        // no-op re-fire loop is bounded instead of running every turn forever.
        var session = CreateSession(
            ("user", "u1"),
            ("assistant", "a1"),
            ("user", "u2"),
            ("assistant", "a2"));
        var compactor = CreateCompactor("unused");
        var options = new CompactionOptions
        {
            PreservedTurns = 3,
            ContextWindowTokens = 1_000_000,
            TokenThresholdRatio = 0.5,
            CircuitBreakerCooldownSeconds = 600,
            SummarizationModel = TestModel.Id
        };

        for (var i = 0; i < LlmSessionCompactor.MaxConsecutiveFailures; i++)
        {
            var attempt = await compactor.CompactAsync(session, options);
            attempt.SkipReason.ShouldBe(CompactionSkipReasons.NoSummarizableTurns);
        }

        var guarded = await compactor.CompactAsync(session, options);

        guarded.Succeeded.ShouldBeFalse();
        guarded.SkipReason.ShouldBe(CompactionSkipReasons.CircuitBreakerOpen);
    }

    // ── Coordinator: reason propagation + warning log ────────────────────────

    [Fact]
    public async Task CoordinatorCompactAsync_AbortedCompaction_PropagatesReasonAndLogsIt()
    {
        var logger = new ListLogger<SessionCompactionCoordinator>();
        var coordinator = CreateCoordinator(
            logger,
            CompactionResult.Skipped(
                entriesPreserved: 440,
                skipReason: CompactionSkipReasons.NoSummarizableTurns));

        var session = CreateSession(("user", "hello"));

        var outcome = await coordinator.CompactAsync(TestAgent, session, CancellationToken.None);

        outcome.Applied.ShouldBeFalse();
        outcome.SkipReason.ShouldBe(CompactionSkipReasons.NoSummarizableTurns);

        var warning = logger.Entries
            .Where(e => e.Level == LogLevel.Warning)
            .Select(e => e.Message)
            .FirstOrDefault(m => m.Contains("compaction did not apply", StringComparison.Ordinal));

        warning.ShouldNotBeNull("an aborted compaction must log a warning naming the abort branch");
        warning.ShouldContain($"reason={CompactionSkipReasons.NoSummarizableTurns}");
    }

    [Fact]
    public async Task CoordinatorCompactAsync_CompactorThrows_ReportsSummarizationFailedReason()
    {
        var logger = new ListLogger<SessionCompactionCoordinator>();
        var compactor = new Mock<ISessionCompactor>();
        compactor
            .Setup(c => c.CompactAsync(It.IsAny<GatewaySession>(), It.IsAny<CompactionOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider exploded"));

        var coordinator = CreateCoordinator(logger, compactor);

        var session = CreateSession(("user", "hello"));

        var outcome = await coordinator.CompactAsync(TestAgent, session, CancellationToken.None);

        outcome.Applied.ShouldBeFalse();
        outcome.SkipReason.ShouldBe(CompactionSkipReasons.SummarizationFailed);
    }

    [Fact]
    public async Task CoordinatorCompactAsync_AppliedCompaction_HasNoSkipReason()
    {
        var logger = new ListLogger<SessionCompactionCoordinator>();
        var session = CreateSession(("user", "hello"));
        var snap = session.SnapshotHistoryForCompaction();

        var coordinator = CreateCoordinator(logger, CompactionResult.ForSuccess(
            summary: "s",
            compactedHistory: [new SessionEntry { Role = MessageRole.System, Content = "summary", IsCompactionSummary = true }],
            entriesSummarized: 1,
            entriesPreserved: 0,
            tokensBefore: 100,
            tokensAfter: 10,
            snapshotDestructiveVersion: snap.DestructiveVersion,
            snapshotHistoryCount: snap.Count));

        var outcome = await coordinator.CompactAsync(TestAgent, session, CancellationToken.None);

        outcome.Applied.ShouldBeTrue();
        outcome.SkipReason.ShouldBeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SessionCompactionCoordinator CreateCoordinator(
        ListLogger<SessionCompactionCoordinator> logger,
        CompactionResult result)
    {
        var compactor = new Mock<ISessionCompactor>();
        compactor
            .Setup(c => c.CompactAsync(It.IsAny<GatewaySession>(), It.IsAny<CompactionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return CreateCoordinator(logger, compactor);
    }

    private static SessionCompactionCoordinator CreateCoordinator(
        ListLogger<SessionCompactionCoordinator> logger,
        Mock<ISessionCompactor> compactor)
    {
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channelManager = new Mock<IChannelManager>();
        var optionsMonitor = new Mock<IOptionsMonitor<CompactionOptions>>();
        optionsMonitor.Setup(o => o.CurrentValue).Returns(new CompactionOptions { PreservedTurns = 3 });

        return new SessionCompactionCoordinator(
            compactor.Object,
            sessions.Object,
            supervisor.Object,
            channelManager.Object,
            optionsMonitor.Object,
            logger);
    }

    private static GatewaySession CreateSession(params (string role, string content)[] entries)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(Guid.NewGuid().ToString("N")),
            AgentId = TestAgent
        };

        session.AddEntries(entries.Select(entry => new SessionEntry
        {
            Role = entry.role,
            Content = entry.content
        }));

        return session;
    }

    private static LlmSessionCompactor CreateCompactor(string summary)
    {
        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();
        models.Register(TestModel.Provider, TestModel);

        var provider = new Mock<IApiProvider>();
        provider.SetupGet(item => item.Api).Returns(TestModel.Api);
        provider.Setup(item => item.StreamSimple(
                It.IsAny<LlmModel>(),
                It.IsAny<Context>(),
                It.IsAny<SimpleStreamOptions?>()))
            .Returns(() => CreateStream(summary));

        providers.Register(provider.Object);

        var llmClient = new LlmClient(providers, models);
        return new LlmSessionCompactor(llmClient, NullLogger<LlmSessionCompactor>.Instance);
    }

    private static LlmStream CreateStream(string summary)
    {
        var stream = new LlmStream();
        var completion = new AssistantMessage(
            Content: string.IsNullOrEmpty(summary) ? [] : [new TextContent(summary)],
            Api: TestModel.Api,
            Provider: TestModel.Provider,
            ModelId: TestModel.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        stream.Push(new DoneEvent(StopReason.Stop, completion));
        stream.End(completion);
        return stream;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
