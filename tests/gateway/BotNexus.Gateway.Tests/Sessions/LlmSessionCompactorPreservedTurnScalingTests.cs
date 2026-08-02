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
/// #2522 unit-normalisation: the compaction trigger fires on one unit (real provider prompt tokens,
/// which include system prompt, tool schemas and workspace-injected files) while the split/cut walk
/// plans the retained tail in a different unit (the local <c>chars/4</c> estimate over LLM-visible
/// entries only). When the provider/estimate ratio is materially above 1 the keep-recent budget is
/// scaled down so the retained tail is sized in the same units the trigger fired in.
///
/// These tests pin three things: the scaling maths, the CAP that stops a small transcript collapsing
/// to a one-entry tail, and the fail-safe requirement that a session with NO provider measurement
/// behaves exactly as it does today.
/// </summary>
public sealed class LlmSessionCompactorPreservedTurnScalingTests
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

    // -- Pure scaling maths --------------------------------------------------

    [Fact]
    public void ScalePreservedTurns_NoProviderCount_ReturnsRequestedTurnsUnchanged()
    {
        // FAIL SAFE, NOT CLOSED: an unmeasurable session must plan its cut exactly as today.
        var measurement = new LlmSessionCompactor.CompactionTokenMeasurement(100, null, null);

        LlmSessionCompactor.ScalePreservedTurns(6, measurement).ShouldBe(6);
    }

    [Fact]
    public void ScalePreservedTurns_RatioNotMateriallyAboveOne_ReturnsRequestedTurnsUnchanged()
    {
        // A 10% divergence is estimator noise, not a unit mismatch. Do not churn the cut plan.
        var measurement = new LlmSessionCompactor.CompactionTokenMeasurement(100, 110, 1.10);

        LlmSessionCompactor.ScalePreservedTurns(6, measurement).ShouldBe(6);
    }

    [Fact]
    public void ScalePreservedTurns_RatioBelowOne_ReturnsRequestedTurnsUnchanged()
    {
        // Provider smaller than the estimate must never GROW the retained tail.
        var measurement = new LlmSessionCompactor.CompactionTokenMeasurement(100, 50, 0.5);

        LlmSessionCompactor.ScalePreservedTurns(6, measurement).ShouldBe(6);
    }

    [Fact]
    public void ScalePreservedTurns_MaterialRatio_ScalesBudgetDownByThatRatio()
    {
        // 4x more real context than the estimator can see => the tail must be ~4x smaller in turns.
        var measurement = new LlmSessionCompactor.CompactionTokenMeasurement(100, 400, 4.0);

        LlmSessionCompactor.ScalePreservedTurns(8, measurement).ShouldBe(2);
    }

    [Fact]
    public void ScalePreservedTurns_MaterialRatio_RoundsUpSoTheTailIsNotOverCut()
    {
        var measurement = new LlmSessionCompactor.CompactionTokenMeasurement(100, 300, 3.0);

        // ceil(8 / 3) == 3, not 2.
        LlmSessionCompactor.ScalePreservedTurns(8, measurement).ShouldBe(3);
    }

    [Fact]
    public void ScalePreservedTurns_ExtremeRatio_IsClampedByTheMaxScaleCap()
    {
        // 1000x is clamped to MaxProviderRatioScale, so a single outlier cannot dominate the plan.
        var measurement = new LlmSessionCompactor.CompactionTokenMeasurement(2, 2000, 1000.0);

        var clamped = new LlmSessionCompactor.CompactionTokenMeasurement(
            2, 2 * (int)LlmSessionCompactor.MaxProviderRatioScale, LlmSessionCompactor.MaxProviderRatioScale);

        LlmSessionCompactor.ScalePreservedTurns(24, measurement)
            .ShouldBe(LlmSessionCompactor.ScalePreservedTurns(24, clamped));
    }

    [Fact]
    public void ScalePreservedTurns_ExtremeRatio_NeverFallsBelowTheMinimumTailFloor()
    {
        var measurement = new LlmSessionCompactor.CompactionTokenMeasurement(2, 2000, 1000.0);

        LlmSessionCompactor.ScalePreservedTurns(3, measurement)
            .ShouldBe(LlmSessionCompactor.MinScaledPreservedTurns);
        LlmSessionCompactor.MinScaledPreservedTurns.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void ScalePreservedTurns_AlreadyAtOrBelowTheFloor_IsNeverIncreased()
    {
        var measurement = new LlmSessionCompactor.CompactionTokenMeasurement(2, 2000, 1000.0);

        LlmSessionCompactor.ScalePreservedTurns(1, measurement).ShouldBe(1);
        LlmSessionCompactor.ScalePreservedTurns(2, measurement).ShouldBe(2);
    }

    [Fact]
    public void ScalePreservedTurns_NonPositiveBudget_IsPassedThroughUnchanged()
    {
        var measurement = new LlmSessionCompactor.CompactionTokenMeasurement(2, 2000, 1000.0);

        LlmSessionCompactor.ScalePreservedTurns(0, measurement).ShouldBe(0);
    }

    // -- End-to-end through CompactAsync -------------------------------------

    [Fact]
    public async Task CompactAsync_ProviderRatioMateriallyAboveOne_ShedsContextInsteadOfAborting()
    {
        // 4 user turns with PreservedTurns=4: the unscaled split finds nothing summarizable and the
        // tail is below the token threshold, so today this session aborts every turn while the REAL
        // provider context is far larger than the estimate. With the budget normalised to provider
        // units the split becomes viable and the session actually sheds context.
        var session = CreateFourTurnSession();
        session.Metadata[LlmSessionCompactor.ProviderPromptTokensMetadataKey] = 400;

        var logger = new ListLogger<LlmSessionCompactor>();
        var compactor = CreateCompactor(logger);

        var result = await compactor.CompactAsync(session, FourTurnOptions());

        result.Succeeded.ShouldBeTrue();
        result.SkipReason.ShouldBeNull();
        result.EntriesPreserved.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task CompactAsync_NoProviderMeasurement_BehaviourIsIdenticalToToday()
    {
        // Fail safe: no provider count => no ratio => no scaling => the exact pre-change outcome.
        var session = CreateFourTurnSession();

        var logger = new ListLogger<LlmSessionCompactor>();
        var compactor = CreateCompactor(logger);

        var result = await compactor.CompactAsync(session, FourTurnOptions());

        result.Succeeded.ShouldBeFalse();
        result.SkipReason.ShouldBe(CompactionSkipReason.NoSummarizableTurns);
        result.EntriesPreserved.ShouldBe(8);
        session.GetHistorySnapshot().Count.ShouldBe(8);
    }

    [Fact]
    public async Task CompactAsync_ExtremeRatioOnTinyTranscript_KeepsAMultiEntryTail()
    {
        // Cap guard, end to end: a 1000x ratio on a 2-turn transcript must not collapse the tail.
        var session = CreateTwoTurnSession();
        session.Metadata[LlmSessionCompactor.ProviderPromptTokensMetadataKey] = 2000;

        var logger = new ListLogger<LlmSessionCompactor>();
        var compactor = CreateCompactor(logger);

        var result = await compactor.CompactAsync(session, TwoTurnOptions());

        result.EntriesPreserved.ShouldBeGreaterThan(1);
        session.GetHistorySnapshot().Count.ShouldBeGreaterThan(1);
    }

    // -- Helpers -------------------------------------------------------------

    private static CompactionOptions FourTurnOptions() => new()
    {
        PreservedTurns = 4,
        ContextWindowTokens = 1_000_000,
        TokenThresholdRatio = 0.5,
        SummarizationModel = TestModel.Id
    };

    private static CompactionOptions TwoTurnOptions() => new()
    {
        PreservedTurns = 3,
        ContextWindowTokens = 1_000_000,
        TokenThresholdRatio = 0.5,
        SummarizationModel = TestModel.Id
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

    private static GatewaySession NewSession() => new()
    {
        SessionId = SessionId.From(Guid.NewGuid().ToString("N")),
        AgentId = TestAgent
    };

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
                    Content: [new TextContent("summary text")],
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
