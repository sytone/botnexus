using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// #3534: the compaction trigger must fire on the PROVIDER's reported prompt-token count, not only
/// on the local <c>chars/4</c> estimate.
/// </summary>
/// <remarks>
/// <para>
/// The estimator counts LLM-visible history entries only. It cannot see the system prompt, the tool
/// schemas, or workspace-injected files, so it systematically under-counts real context. The
/// provider's <c>lastProviderPromptTokens</c> is the ground truth for what the previous call
/// actually cost. Before this change that number was read by <c>MeasureTokens</c> and used to
/// normalise the cut plan, but nothing consumed it as a trigger - so a session could sit at 999,306
/// provider prompt tokens against a 120,000 threshold, never compact, and then return empty
/// completions once the window was exhausted.
/// </para>
/// <para>
/// Non-vacuity: <see cref="ShouldCompact_ProviderCountOverThreshold_TriggersEvenWhenEstimateIsTiny"/>
/// constructs a session whose estimate is ~2 tokens - orders of magnitude BELOW the threshold - so
/// it can only pass if the provider count is genuinely consulted. It fails against the pre-#3534
/// implementation.
/// </para>
/// </remarks>
public sealed class LlmSessionCompactorProviderTriggerTests
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

    [Fact]
    public void ShouldCompact_ProviderCountOverThreshold_TriggersEvenWhenEstimateIsTiny()
    {
        // The reproduction of the filed incident, in miniature: a trivially small visible history
        // (estimate ~2 tokens, far under the 120,000 threshold) paired with the provider count
        // actually observed on the wedged session. Only a provider-aware trigger can fire here.
        var session = CreateSmallSession();
        session.Metadata[LlmSessionCompactor.ProviderPromptTokensMetadataKey] = 999_306;

        var logger = new ListLogger<LlmSessionCompactor>();
        var compactor = CreateCompactor(logger);

        compactor.ShouldCompact(session.Session, LiveLikeOptions()).ShouldBeTrue();
    }

    [Fact]
    public void ShouldCompact_ProviderCountUnderThreshold_DoesNotTrigger()
    {
        // Guards against the fix degenerating into "always compact".
        var session = CreateSmallSession();
        session.Metadata[LlmSessionCompactor.ProviderPromptTokensMetadataKey] = 1_000;

        var compactor = CreateCompactor(new ListLogger<LlmSessionCompactor>());

        compactor.ShouldCompact(session.Session, LiveLikeOptions()).ShouldBeFalse();
    }

    [Fact]
    public void ShouldCompact_NoProviderCount_BehavesExactlyAsBefore()
    {
        // An unmeasured session must be unaffected by this change: null means "unavailable",
        // never zero and never a trigger.
        var session = CreateSmallSession();

        var compactor = CreateCompactor(new ListLogger<LlmSessionCompactor>());

        compactor.ShouldCompact(session.Session, LiveLikeOptions()).ShouldBeFalse();
    }

    [Fact]
    public void ShouldCompact_WhenTriggered_LogsDecisionAtInformationNotDebug()
    {
        // AC2: the decision that fires must survive production log levels. The original incident
        // left ZERO forensic trace because this line was Debug-only.
        var session = CreateSmallSession();
        session.Metadata[LlmSessionCompactor.ProviderPromptTokensMetadataKey] = 999_306;

        var logger = new ListLogger<LlmSessionCompactor>();
        var compactor = CreateCompactor(logger);

        compactor.ShouldCompact(session.Session, LiveLikeOptions()).ShouldBeTrue();

        var entry = logger.Entries
            .Where(e => e.Level == LogLevel.Information)
            .Select(e => e.Message)
            .Single(m => m.Contains("ShouldCompact check", StringComparison.Ordinal));

        // The three numbers needed to diagnose a stuck session without a SQLite query.
        entry.ShouldContain("providerPromptTokens 999306");
        entry.ShouldContain("providerTrigger True");
        entry.ShouldContain("threshold 120000");
    }

    [Fact]
    public void ShouldCompact_WhenNotTriggered_StaysAtDebugSoSteadyStateVolumeIsUnchanged()
    {
        var session = CreateSmallSession();

        var logger = new ListLogger<LlmSessionCompactor>();
        var compactor = CreateCompactor(logger);

        compactor.ShouldCompact(session.Session, LiveLikeOptions()).ShouldBeFalse();

        logger.Entries.ShouldNotContain(e =>
            e.Level == LogLevel.Information && e.Message.Contains("ShouldCompact check", StringComparison.Ordinal));
        logger.Entries.ShouldContain(e =>
            e.Level == LogLevel.Debug && e.Message.Contains("ShouldCompact check", StringComparison.Ordinal));
    }

    [Fact]
    public void EvaluateTokenTriggers_ReportsEachUnitIndependently()
    {
        var options = LiveLikeOptions();

        var providerOnly = LlmSessionCompactor.EvaluateTokenTriggers(
            new LlmSessionCompactor.CompactionTokenMeasurement(EstimatedTokens: 2, ProviderPromptTokens: 999_306, Ratio: null),
            options);
        providerOnly.estimateTrigger.ShouldBeFalse();
        providerOnly.providerTrigger.ShouldBeTrue();
        providerOnly.threshold.ShouldBe(120_000);

        var estimateOnly = LlmSessionCompactor.EvaluateTokenTriggers(
            new LlmSessionCompactor.CompactionTokenMeasurement(EstimatedTokens: 130_000, ProviderPromptTokens: null, Ratio: null),
            options);
        estimateOnly.estimateTrigger.ShouldBeTrue();
        estimateOnly.providerTrigger.ShouldBeFalse();

        var neither = LlmSessionCompactor.EvaluateTokenTriggers(
            new LlmSessionCompactor.CompactionTokenMeasurement(EstimatedTokens: 10, ProviderPromptTokens: 20, Ratio: 2.0),
            options);
        neither.estimateTrigger.ShouldBeFalse();
        neither.providerTrigger.ShouldBeFalse();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static GatewaySession CreateSmallSession()
    {
        // Four 2-char entries => 8 visible chars => 2 estimated tokens.
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
