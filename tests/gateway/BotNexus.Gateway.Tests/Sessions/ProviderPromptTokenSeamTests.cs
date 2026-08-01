using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Streaming;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// #2522 producer seam, end to end. PR #2531 shipped the compactor's measure-first READ of
/// <c>lastProviderPromptTokens</c>, but nothing in the repository wrote that key, so every
/// compaction abort rendered <c>providerPromptTokens=unavailable</c> in production and the
/// diagnostic could never fire. These tests drive a real streamed turn through
/// <see cref="StreamingSessionHelper"/> with a KNOWN provider prompt-token count and then assert
/// the SUBSEQUENT compaction abort emits that exact number and a real ratio - i.e. they assert the
/// observable log line, not that a metadata field was assigned.
/// </summary>
public sealed class ProviderPromptTokenSeamTests
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

    // The streamed turn leaves four 2-char visible entries => 8 chars => 8/4 = 2 estimated tokens.
    private const int ExpectedEstimatedTokens = 2;

    [Fact]
    public async Task StreamedTurnWithProviderUsage_ThenCompactionAbort_EmitsRealProviderTokensAndRatio()
    {
        var session = CreateSessionWithThreePriorEntries();
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.MessageStart, MessageId = "m1" },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "a2", MessageId = "m1" },
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.MessageEnd,
                    MessageId = "m1",
                    Usage = new AgentResponseUsage(InputTokens: 20, OutputTokens: 7)
                }
            ]),
            session,
            store.Object);

        session.GetHistorySnapshot().Count.ShouldBe(4);

        var logger = new ListLogger<LlmSessionCompactor>();
        var compactor = CreateCompactor(logger);

        var result = await compactor.CompactAsync(session, StuckOptions());

        result.SkipReason.ShouldBe(CompactionSkipReason.NoSummarizableTurns);

        var warning = SingleAbortWarning(logger);
        warning.ShouldContain($"estimated={ExpectedEstimatedTokens}");
        warning.ShouldContain("providerPromptTokens=20");
        // 20 / 2 == 10.00 exactly. Would render "unavailable" if the producer seam were absent.
        warning.ShouldContain("providerToEstimateRatio=10.00");
        warning.ShouldNotContain("providerPromptTokens=unavailable");
    }

    [Fact]
    public async Task StreamedTurnWithCacheAwareUsage_ThenCompactionAbort_CountsCachedPromptTokens()
    {
        // Cache-aware providers report the cached portion of the prompt separately from
        // input_tokens, but the model still saw it - so the recorded prompt cost is the sum.
        var session = CreateSessionWithThreePriorEntries();
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "a2", MessageId = "m1" },
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.MessageEnd,
                    MessageId = "m1",
                    Usage = new AgentResponseUsage(InputTokens: 4, OutputTokens: 1, CacheRead: 10, CacheWrite: 6)
                }
            ]),
            session,
            store.Object);

        var logger = new ListLogger<LlmSessionCompactor>();
        var compactor = CreateCompactor(logger);

        await compactor.CompactAsync(session, StuckOptions());

        var warning = SingleAbortWarning(logger);
        warning.ShouldContain("providerPromptTokens=20");
        warning.ShouldContain("providerToEstimateRatio=10.00");
    }

    [Fact]
    public async Task StreamedTurnWithoutProviderUsage_LeavesMeasurementUnavailable()
    {
        // A provider that reports nothing must not fabricate a zero: absence stays absence, and
        // the compactor keeps saying so honestly.
        var session = CreateSessionWithThreePriorEntries();
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "a2", MessageId = "m1" },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd, MessageId = "m1", Usage = null }
            ]),
            session,
            store.Object);

        var logger = new ListLogger<LlmSessionCompactor>();
        var compactor = CreateCompactor(logger);

        await compactor.CompactAsync(session, StuckOptions());

        var warning = SingleAbortWarning(logger);
        warning.ShouldContain("providerPromptTokens=unavailable");
        warning.ShouldContain("providerToEstimateRatio=unavailable");
    }

    [Fact]
    public async Task SecondStreamedTurn_OverwritesPriorProviderPromptTokens()
    {
        // The recorded value is the LAST reported prompt cost, not the first: a stale first-turn
        // number would make the ratio meaningless later in a long session.
        var session = CreateSessionWithThreePriorEntries();
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.MessageEnd,
                    MessageId = "m1",
                    Usage = new AgentResponseUsage(InputTokens: 999)
                }
            ]),
            session,
            store.Object);

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "a2", MessageId = "m2" },
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.MessageEnd,
                    MessageId = "m2",
                    Usage = new AgentResponseUsage(InputTokens: 20)
                }
            ]),
            session,
            store.Object);

        var logger = new ListLogger<LlmSessionCompactor>();
        var compactor = CreateCompactor(logger);

        await compactor.CompactAsync(session, StuckOptions());

        var warning = SingleAbortWarning(logger);
        warning.ShouldContain("providerPromptTokens=20");
        warning.ShouldNotContain("providerPromptTokens=999");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<AgentStreamEvent> ToAsyncEnumerable(AgentStreamEvent[] events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }

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

    private static GatewaySession CreateSessionWithThreePriorEntries()
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(Guid.NewGuid().ToString("N")),
            AgentId = TestAgent
        };

        session.AddEntries(new[]
        {
            new SessionEntry { Role = MessageRole.User, Content = "u1" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a1" },
            new SessionEntry { Role = MessageRole.User, Content = "u2" }
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
