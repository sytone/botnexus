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
/// #3592: <c>lastProviderPromptTokens</c> describes the LAST COMPLETED PROVIDER REQUEST, not the
/// current context. The moment a successful compaction mutates history between two requests those
/// two things diverge, and because the only writer
/// (<c>ProviderTokenUsageRecorder.Record</c>) refuses to store a non-positive value, no code path
/// could ever reset it through the producer.
/// </summary>
/// <remarks>
/// <para>
/// The consequence is the #1574 cascade re-entering through metadata lifetime rather than through
/// arithmetic: after the cut, <c>ShouldCompact</c> keeps firing on the stale pre-cut number until
/// the next completed provider turn, the <c>toSummarize.Count == 0</c> fallback engages, and
/// <c>PreservedTurns</c> is walked down to 1 trying to shed context that is already shed.
/// </para>
/// <para>
/// The fix clears the key at the SUCCESSFUL commit point only. These tests pin all three halves of
/// that contract: cleared on success, left untouched on every Skipped/Failed outcome (no context was
/// shed, so the number is still accurate), and absence remaining "unavailable" rather than becoming
/// a zero that could be mistaken for a measurement.
/// </para>
/// </remarks>
public sealed class LlmSessionCompactorProviderTokenStalenessTests
{
    private const string Key = LlmSessionCompactor.ProviderPromptTokensMetadataKey;

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

    /// <summary>Window 200000 * ratio 0.6 => 120000, mirroring the live configuration.</summary>
    private static CompactionOptions LiveLikeOptions() => new()
    {
        PreservedTurns = 3,
        ContextWindowTokens = 200_000,
        TokenThresholdRatio = 0.6,
        // Disable the #1599 byte trigger so these tests isolate the TOKEN decision only.
        LargestEntryBytesThreshold = 0,
        SummarizationModel = TestModel.Id
    };

    // ── AC1: cleared on a successful cut ─────────────────────────────────────

    [Fact]
    public async Task CompactAsync_Success_ClearsTheStaleProviderPromptTokenCount()
    {
        var session = CreateFourTurnSession();
        session.Metadata[Key] = 999_306;

        var compactor = CreateCompactor(SummarizingProvider());

        var result = await compactor.CompactAsync(session, LiveLikeOptions());

        result.Succeeded.ShouldBeTrue();
        session.Metadata.ContainsKey(Key).ShouldBeFalse();
    }

    [Fact]
    public async Task ShouldCompact_AfterASuccessfulCut_NoLongerFiresOnTheStaleProviderCount()
    {
        // The defect in miniature. The visible estimate is ~2 tokens either side of the cut, so the
        // ONLY thing that can keep ShouldCompact true afterwards is the stale provider number.
        var session = CreateFourTurnSession();
        session.Metadata[Key] = 999_306;

        var compactor = CreateCompactor(SummarizingProvider());
        var options = LiveLikeOptions();

        compactor.ShouldCompact(session.Session, options).ShouldBeTrue();

        var result = await compactor.CompactAsync(session, options);
        result.Succeeded.ShouldBeTrue();

        compactor.ShouldCompact(session.Session, options).ShouldBeFalse();
    }

    [Fact]
    public async Task CompactAsync_Success_LeavesUnrelatedMetadataUntouched()
    {
        // Minimality: clear exactly one key, not the metadata bag.
        var session = CreateFourTurnSession();
        session.Metadata[Key] = 999_306;
        session.Metadata["modelOverride"] = "some-model";

        var compactor = CreateCompactor(SummarizingProvider());

        (await compactor.CompactAsync(session, LiveLikeOptions())).Succeeded.ShouldBeTrue();

        session.Metadata.ContainsKey(Key).ShouldBeFalse();
        session.Metadata["modelOverride"].ShouldBe("some-model");
    }

    // ── AC2: untouched on Skipped / Failed (non-vacuity) ─────────────────────

    [Fact]
    public async Task CompactAsync_SkippedOnEmptyHistory_LeavesTheProviderCountUnchanged()
    {
        // No context was shed, so the number still describes the current context accurately.
        var session = NewSession();
        session.Metadata[Key] = 999_306;

        var compactor = CreateCompactor(SummarizingProvider());

        var result = await compactor.CompactAsync(session, LiveLikeOptions());

        result.Succeeded.ShouldBeFalse();
        result.SkipReason.ShouldBe(CompactionSkipReason.EmptyHistory);
        session.Metadata[Key].ShouldBe(999_306);
    }

    [Fact]
    public async Task CompactAsync_SkippedOnNoSummarizableTurns_LeavesTheProviderCountUnchanged()
    {
        // A two-turn session with PreservedTurns=3 and both triggers under threshold: nothing to
        // summarise, nothing shed, so the measurement must survive.
        var session = CreateTwoTurnSession();
        session.Metadata[Key] = 1_000;

        var compactor = CreateCompactor(SummarizingProvider());

        var result = await compactor.CompactAsync(session, LiveLikeOptions());

        result.Succeeded.ShouldBeFalse();
        result.SkipReason.ShouldBe(CompactionSkipReason.NoSummarizableTurns);
        session.Metadata[Key].ShouldBe(1_000);
    }

    [Fact]
    public async Task CompactAsync_SkippedOnEmptySummary_LeavesTheProviderCountUnchanged()
    {
        // History is explicitly NOT mutated on this path, so clearing here would discard a still-
        // valid measurement and blind the trigger for a turn.
        var session = CreateFourTurnSession();
        session.Metadata[Key] = 999_306;

        var compactor = CreateCompactor(SummarizingProvider(summary: "   "));

        var result = await compactor.CompactAsync(session, LiveLikeOptions());

        result.Succeeded.ShouldBeFalse();
        result.SkipReason.ShouldBe(CompactionSkipReason.EmptySummary);
        session.Metadata[Key].ShouldBe(999_306);
    }

    [Fact]
    public async Task CompactAsync_SummarizationFailure_LeavesTheProviderCountUnchanged()
    {
        var session = CreateFourTurnSession();
        session.Metadata[Key] = 999_306;

        var compactor = CreateCompactor(ThrowingProvider());

        var result = await compactor.CompactAsync(session, LiveLikeOptions());

        result.Succeeded.ShouldBeFalse();
        result.SkipReason.ShouldBe(CompactionSkipReason.SummarizationFailed);
        session.Metadata[Key].ShouldBe(999_306);
    }

    // ── AC3: absence stays "unavailable", never zero ─────────────────────────

    [Fact]
    public async Task CompactAsync_WithNoProviderCount_NeverIntroducesTheKey()
    {
        // Absence is the well-defined "unavailable" state on the read side. Clearing an absent key
        // must not stamp a zero that MeasureTokens would have to defend against.
        var session = CreateFourTurnSession();

        var compactor = CreateCompactor(SummarizingProvider());
        var options = LiveLikeOptions();

        session.Metadata.ContainsKey(Key).ShouldBeFalse();
        compactor.ShouldCompact(session.Session, options).ShouldBeFalse();

        var result = await compactor.CompactAsync(session, options);
        result.Succeeded.ShouldBeTrue();

        session.Metadata.ContainsKey(Key).ShouldBeFalse();
        compactor.ShouldCompact(session.Session, options).ShouldBeFalse();
    }

    [Fact]
    public async Task CompactAsync_WithNoProviderCount_SkippedPathAlsoLeavesTheKeyAbsent()
    {
        var session = CreateTwoTurnSession();

        var compactor = CreateCompactor(SummarizingProvider());

        var result = await compactor.CompactAsync(session, LiveLikeOptions());

        result.Succeeded.ShouldBeFalse();
        session.Metadata.ContainsKey(Key).ShouldBeFalse();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static GatewaySession NewSession() => new()
    {
        SessionId = SessionId.From(Guid.NewGuid().ToString("N")),
        AgentId = TestAgent
    };

    private static GatewaySession CreateFourTurnSession()
    {
        var session = NewSession();
        session.AddEntries(new[]
        {
            new SessionEntry { Role = MessageRole.User, Content = "u1" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a1" },
            new SessionEntry { Role = MessageRole.User, Content = "u2" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a2" },
            new SessionEntry { Role = MessageRole.User, Content = "u3" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a3" },
            new SessionEntry { Role = MessageRole.User, Content = "u4" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a4" }
        });
        return session;
    }

    private static GatewaySession CreateTwoTurnSession()
    {
        var session = NewSession();
        session.AddEntries(new[]
        {
            new SessionEntry { Role = MessageRole.User, Content = "u1" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a1" },
            new SessionEntry { Role = MessageRole.User, Content = "u2" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a2" }
        });
        return session;
    }

    private static Mock<IApiProvider> SummarizingProvider(string summary = "summary text")
    {
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
                    Content: [new TextContent(summary)],
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
        return provider;
    }

    private static Mock<IApiProvider> ThrowingProvider()
    {
        var provider = new Mock<IApiProvider>();
        provider.SetupGet(item => item.Api).Returns(TestModel.Api);
        provider.Setup(item => item.StreamSimple(
                It.IsAny<LlmModel>(),
                It.IsAny<Context>(),
                It.IsAny<SimpleStreamOptions?>()))
            .Throws(new InvalidOperationException("provider exploded"));
        return provider;
    }

    private static LlmSessionCompactor CreateCompactor(Mock<IApiProvider> provider)
    {
        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();
        models.Register(TestModel.Provider, TestModel);
        providers.Register(provider.Object);
        return new LlmSessionCompactor(new LlmClient(providers, models), new ListLogger<LlmSessionCompactor>());
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
