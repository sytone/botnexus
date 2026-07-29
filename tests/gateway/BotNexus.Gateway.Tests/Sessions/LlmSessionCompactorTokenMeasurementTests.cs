using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// #2522 measure-first: the compaction decision must emit BOTH token numbers it is (or could be)
/// made against — the local <c>chars/4</c> estimator over LLM-visible entries and the provider's
/// reported prompt-token count — plus their ratio, so a repeating
/// <see cref="CompactionSkipReason.NoSummarizableTurns"/> abort loop is self-diagnosing from a
/// single log line. These tests assert the SPECIFIC emitted numbers, not merely that a warning
/// was logged.
/// </summary>
public sealed class LlmSessionCompactorTokenMeasurementTests
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

    // Four 2-char entries => 8 visible chars => 8/4 = 2 estimated tokens. Deterministic on purpose
    // so the emitted ratio below is an exact string, not a range.
    private const int ExpectedEstimatedTokens = 2;

    [Fact]
    public async Task CompactAsync_NoSummarizableTurns_WithProviderPromptTokens_LogsBothNumbersAndExactRatio()
    {
        var session = CreateStuckSession();
        session.Metadata[LlmSessionCompactor.ProviderPromptTokensMetadataKey] = 20;

        var logger = new ListLogger<LlmSessionCompactor>();
        var compactor = CreateCompactor(logger);

        var result = await compactor.CompactAsync(session, StuckOptions());

        // The specific reason code (wire contract, #2489) is unchanged by the enrichment.
        result.Succeeded.ShouldBeFalse();
        result.SkipReason.ShouldBe(CompactionSkipReason.NoSummarizableTurns);
        result.EntriesPreserved.ShouldBe(4);

        var warning = SingleAbortWarning(logger);
        warning.ShouldContain($"estimated={ExpectedEstimatedTokens}");
        warning.ShouldContain("providerPromptTokens=20");
        // 20 / 2 == 10.00 exactly.
        warning.ShouldContain("providerToEstimateRatio=10.00");
    }

    [Fact]
    public async Task CompactAsync_NoSummarizableTurns_WithoutProviderPromptTokens_ReportsMeasurementUnavailable()
    {
        // Today no producer writes the provider prompt-token count onto the session (repo-wide
        // search under Sessions returns zero hits), so the warning must say so explicitly rather
        // than emit a fabricated ratio of 1.
        var session = CreateStuckSession();

        var logger = new ListLogger<LlmSessionCompactor>();
        var compactor = CreateCompactor(logger);

        var result = await compactor.CompactAsync(session, StuckOptions());

        result.SkipReason.ShouldBe(CompactionSkipReason.NoSummarizableTurns);

        var warning = SingleAbortWarning(logger);
        warning.ShouldContain($"estimated={ExpectedEstimatedTokens}");
        warning.ShouldContain("providerPromptTokens=unavailable");
        warning.ShouldContain("providerToEstimateRatio=unavailable");
    }

    [Fact]
    public async Task CompactAsync_HugeProviderToEstimateRatio_DoesNotCollapseSmallTranscriptTail()
    {
        // Cap guard: an extreme provider/estimate ratio (1000x) on a tiny transcript must NOT shrink
        // the retained tail. Measurement is diagnostic-only in this change — no budget scaling is
        // applied — so the preserved tail stays the full 4 visible entries, never 0 and never 1.
        var session = CreateStuckSession();
        session.Metadata[LlmSessionCompactor.ProviderPromptTokensMetadataKey] = 2000;

        var logger = new ListLogger<LlmSessionCompactor>();
        var compactor = CreateCompactor(logger);

        var result = await compactor.CompactAsync(session, StuckOptions());

        result.SkipReason.ShouldBe(CompactionSkipReason.NoSummarizableTurns);
        result.EntriesPreserved.ShouldBe(4);
        result.EntriesPreserved.ShouldBeGreaterThan(1);
        session.GetHistorySnapshot().Count.ShouldBe(4);

        var warning = SingleAbortWarning(logger);
        warning.ShouldContain("providerToEstimateRatio=1000.00");
    }

    [Fact]
    public void MeasureTokens_ProviderCountPresent_ComputesRatioFromBothUnits()
    {
        var session = CreateStuckSession();
        session.Metadata[LlmSessionCompactor.ProviderPromptTokensMetadataKey] = 15;

        var measurement = LlmSessionCompactor.MeasureTokens(session.Session, ExpectedEstimatedTokens);

        measurement.EstimatedTokens.ShouldBe(2);
        measurement.ProviderPromptTokens.ShouldBe(15);
        measurement.Ratio.ShouldBe(7.5);
        measurement.RatioDisplay.ShouldBe("7.50");
        measurement.ProviderPromptTokensDisplay.ShouldBe("15");
    }

    [Fact]
    public void MeasureTokens_ProviderCountAbsent_YieldsNullRatioNotOne()
    {
        var session = CreateStuckSession();

        var measurement = LlmSessionCompactor.MeasureTokens(session.Session, ExpectedEstimatedTokens);

        measurement.ProviderPromptTokens.ShouldBeNull();
        measurement.Ratio.ShouldBeNull();
        measurement.RatioDisplay.ShouldBe("unavailable");
        measurement.ProviderPromptTokensDisplay.ShouldBe("unavailable");
    }

    [Fact]
    public void MeasureTokens_ProviderCountAsString_IsParsed()
    {
        var session = CreateStuckSession();
        session.Metadata[LlmSessionCompactor.ProviderPromptTokensMetadataKey] = "8";

        var measurement = LlmSessionCompactor.MeasureTokens(session.Session, ExpectedEstimatedTokens);

        measurement.ProviderPromptTokens.ShouldBe(8);
        measurement.Ratio.ShouldBe(4.0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string SingleAbortWarning(ListLogger<LlmSessionCompactor> logger)
    {
        var warning = logger.Entries
            .Where(e => e.Level == LogLevel.Warning)
            .Select(e => e.Message)
            .Single(m => m.Contains(CompactionSkipReason.NoSummarizableTurns.Value, StringComparison.Ordinal));

        warning.ShouldContain("Token measurement:");
        return warning;
    }

    private static CompactionOptions StuckOptions() => new()
    {
        PreservedTurns = 3,
        ContextWindowTokens = 1_000_000,
        TokenThresholdRatio = 0.5,
        SummarizationModel = TestModel.Id
    };

    private static GatewaySession CreateStuckSession()
    {
        // Two user turns with PreservedTurns=3: the split finds nothing summarizable and the tail
        // sits below threshold so the #1574 fallback does not engage. This is the observed loop.
        var session = new GatewaySession
        {
            SessionId = SessionId.From(Guid.NewGuid().ToString("N")),
            AgentId = TestAgent
        };

        session.AddEntries(new[]
        {
            new SessionEntry { Role = MessageRole.User, Content = "u1" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a1" },
            new SessionEntry { Role = MessageRole.User, Content = "u2" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a2" }
        });

        return session;
    }

    private static LlmSessionCompactor CreateCompactor(ILogger<LlmSessionCompactor> logger)
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
            .Returns(() =>
            {
                var stream = new LlmStream();
                var completion = new AssistantMessage(
                    Content: [new TextContent("unused")],
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
            });

        providers.Register(provider.Object);

        return new LlmSessionCompactor(new LlmClient(providers, models), logger);
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
