using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Tests for the compaction resilience changes (PR 2): summary-model fallback and the time-based
/// circuit breaker that auto-resets after a cooldown instead of staying open until gateway restart.
/// </summary>
public sealed class LlmSessionCompactorResilienceTests
{
    // Primary model (e.g. the session's opus) that 421s on the compaction call.
    private static readonly LlmModel PrimaryModel = new(
        Id: "primary-model", Name: "Primary", Api: "primary-api", Provider: "primary-provider",
        BaseUrl: "https://primary.example.com", Reasoning: false, Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 32000, MaxTokens: 4096);

    // A registered default-waterfall fallback model id (must be in DefaultSummaryModelIds).
    private static readonly LlmModel FallbackModel = new(
        Id: "claude-haiku-4.5", Name: "Haiku", Api: "fallback-api", Provider: "fallback-provider",
        BaseUrl: "https://fallback.example.com", Reasoning: false, Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 32000, MaxTokens: 4096);

    // ── Model fallback ────────────────────────────────────────────────────────

    [Fact]
    public async Task CompactAsync_PrimaryModel421_FallsBackToSecondModel_AndSucceeds()
    {
        var session = CreateLargeSession(300);

        // Primary returns an error (simulating HTTP 421 surfaced as StopReason.Error).
        // Fallback (claude-haiku-4.5) returns a real summary.
        var compactor = CreateCompactorWithModels(
            (PrimaryModel, ErrorStream),
            (FallbackModel, () => SuccessStream("fallback summary")));

        var options = new CompactionOptions
        {
            ContextWindowTokens = 100,
            TokenThresholdRatio = 0.01,
            PreservedTurns = 2,
            MaxSummaryChars = 5000,
            SummarizationModel = PrimaryModel.Id,
            SummarizationProvider = PrimaryModel.Provider
        };

        var result = await compactor.CompactAsync(session, options);

        result.Succeeded.ShouldBeTrue();
        result.Summary.ShouldContain("fallback summary");
    }

    [Fact]
    public async Task CompactAsync_AllModelsFail_AbortsWithoutMutatingHistory()
    {
        var session = CreateLargeSession(300);
        var before = session.GetHistorySnapshot().Count;

        // Both primary and fallback error out.
        var compactor = CreateCompactorWithModels(
            (PrimaryModel, ErrorStream),
            (FallbackModel, ErrorStream));

        var options = new CompactionOptions
        {
            ContextWindowTokens = 100,
            TokenThresholdRatio = 0.01,
            PreservedTurns = 2,
            MaxSummaryChars = 5000,
            SummarizationModel = PrimaryModel.Id,
            SummarizationProvider = PrimaryModel.Provider
        };

        var result = await compactor.CompactAsync(session, options);

        result.Succeeded.ShouldBeFalse();
        // History must be untouched when every candidate fails.
        session.GetHistorySnapshot().Count.ShouldBe(before);
    }

    [Fact]
    public void BuildCandidateModels_PutsPrimaryFirst_ThenDedupedFallbacks()
    {
        var compactor = CreateCompactorWithModels(
            (PrimaryModel, ErrorStream),
            (FallbackModel, () => SuccessStream("x")));

        var candidates = compactor.BuildCandidateModels(PrimaryModel.Id, PrimaryModel.Provider);

        candidates.Count.ShouldBe(2);
        candidates[0].Id.ShouldBe(PrimaryModel.Id);       // requested model first
        candidates[1].Id.ShouldBe(FallbackModel.Id);      // default-waterfall fallback next
    }

    [Fact]
    public void BuildCandidateModels_PrimaryAlsoInDefaults_IsNotDuplicated()
    {
        // When the primary IS a default model, it must appear exactly once.
        var compactor = CreateCompactorWithModels(
            (FallbackModel, () => SuccessStream("x")));

        var candidates = compactor.BuildCandidateModels(FallbackModel.Id, FallbackModel.Provider);

        candidates.Count.ShouldBe(1);
        candidates[0].Id.ShouldBe(FallbackModel.Id);
    }

    // ── Time-based circuit breaker ──────────────────────────────────────────────

    [Fact]
    public async Task CircuitBreaker_OpensAfterMaxFailures_WithinCooldown()
    {
        var session = CreateLargeSession(300);
        var compactor = CreateCompactorWithModels((PrimaryModel, ErrorStream));
        var options = BreakerOptions(cooldownSeconds: 600);

        for (var i = 0; i < LlmSessionCompactor.MaxConsecutiveFailures; i++)
            (await compactor.CompactAsync(session, options)).Succeeded.ShouldBeFalse();

        // Breaker open: short-circuits without snapshotting (EntriesPreserved == 0).
        var blocked = await compactor.CompactAsync(session, options);
        blocked.Succeeded.ShouldBeFalse();
        blocked.EntriesPreserved.ShouldBe(0);
    }

    [Fact]
    public async Task CircuitBreaker_AutoResets_AfterCooldownElapses()
    {
        var session = CreateLargeSession(300);
        var compactor = CreateCompactorWithModels((PrimaryModel, ErrorStream));

        // 1-second cooldown so the test can deterministically cross it with a short real delay.
        var options = BreakerOptions(cooldownSeconds: 1);

        // Trip the breaker.
        for (var i = 0; i < LlmSessionCompactor.MaxConsecutiveFailures; i++)
            await compactor.CompactAsync(session, options);

        // Immediately after tripping, the breaker is open → short-circuit (EntriesPreserved == 0).
        var whileOpen = await compactor.CompactAsync(session, options);
        whileOpen.EntriesPreserved.ShouldBe(0);

        // Wait out the cooldown, then it should attempt again (no longer short-circuits): the LLM
        // call runs, fails (empty), and the result snapshots the history → EntriesPreserved > 0.
        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        var afterCooldown = await compactor.CompactAsync(session, options);
        afterCooldown.Succeeded.ShouldBeFalse();        // still failing (model still errors)
        afterCooldown.EntriesPreserved.ShouldBeGreaterThan(0); // but it DID attempt (didn't short-circuit)
    }

    [Fact]
    public async Task CircuitBreaker_CooldownZeroOrNegative_FallsBackToDefault_StaysOpen()
    {
        var session = CreateLargeSession(300);
        var compactor = CreateCompactorWithModels((PrimaryModel, ErrorStream));
        // Invalid cooldown (0) must fall back to the 600s default → breaker stays open in-test.
        var options = BreakerOptions(cooldownSeconds: 0);

        for (var i = 0; i < LlmSessionCompactor.MaxConsecutiveFailures; i++)
            await compactor.CompactAsync(session, options);

        var blocked = await compactor.CompactAsync(session, options);
        blocked.EntriesPreserved.ShouldBe(0); // short-circuited → default cooldown applied, not 0
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static CompactionOptions BreakerOptions(int cooldownSeconds) => new()
    {
        ContextWindowTokens = 100,
        TokenThresholdRatio = 0.01,
        PreservedTurns = 2,
        MaxSummaryChars = 5000,
        SummarizationModel = PrimaryModel.Id,
        SummarizationProvider = PrimaryModel.Provider,
        CircuitBreakerCooldownSeconds = cooldownSeconds
    };

    private static LlmStream SuccessStream(string summary)
    {
        var stream = new LlmStream();
        var completion = new AssistantMessage(
            Content: [new TextContent(summary)],
            Api: "any", Provider: "any", ModelId: "any",
            Usage: Usage.Empty(), StopReason: StopReason.Stop,
            ErrorMessage: null, ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        stream.Push(new DoneEvent(StopReason.Stop, completion));
        stream.End(completion);
        return stream;
    }

    // Simulates the HTTP 421 path: empty content + StopReason.Error + an error message, exactly as
    // the provider surfaces a failed response into the stream result.
    private static LlmStream ErrorStream()
    {
        var stream = new LlmStream();
        var completion = new AssistantMessage(
            Content: [],
            Api: "any", Provider: "any", ModelId: "any",
            Usage: Usage.Empty(), StopReason: StopReason.Error,
            ErrorMessage: "HTTP 421: Misdirected Request", ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        stream.Push(new ErrorEvent(StopReason.Error, completion));
        stream.End(completion);
        return stream;
    }

    /// <summary>
    /// Builds a compactor whose LLM client routes each registered model to its own scripted stream
    /// factory, so per-model success/failure can be controlled for fallback tests.
    /// </summary>
    private static LlmSessionCompactor CreateCompactorWithModels(
        params (LlmModel model, Func<LlmStream> streamFactory)[] modelStreams)
    {
        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();

        // Group by Api so each provider mock answers for all models sharing its Api id.
        foreach (var group in modelStreams.GroupBy(m => m.model.Api))
        {
            var byModelId = group.ToDictionary(
                m => m.model.Id, m => m.streamFactory, StringComparer.OrdinalIgnoreCase);

            foreach (var (model, _) in group)
                models.Register(model.Provider, model);

            var provider = new Mock<IApiProvider>();
            provider.SetupGet(p => p.Api).Returns(group.Key);
            provider.Setup(p => p.StreamSimple(
                    It.IsAny<LlmModel>(), It.IsAny<Context>(), It.IsAny<SimpleStreamOptions?>()))
                .Returns((LlmModel m, Context _, SimpleStreamOptions? _) =>
                    byModelId.TryGetValue(m.Id, out var factory)
                        ? factory()
                        : SuccessStream("unexpected-model"));
            providers.Register(provider.Object);
        }

        var llmClient = new LlmClient(providers, models);
        return new LlmSessionCompactor(llmClient, NullLogger<LlmSessionCompactor>.Instance);
    }

    private static GatewaySession CreateLargeSession(int entryCount)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(Guid.NewGuid().ToString("N")),
            AgentId = AgentId.From("agent")
        };
        var entries = new List<SessionEntry>();
        for (var i = 0; i < entryCount; i++)
        {
            entries.Add(new SessionEntry
            {
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"message {i} " + new string('x', 50)
            });
        }
        session.AddEntries(entries);
        return session;
    }
}
