using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// #2556: <see cref="CompactionSkipReason.SummarizationFailed"/> existed but had no producer in
/// <see cref="LlmSessionCompactor"/> - the only catch around the summarization call was the
/// <see cref="OperationCanceledException"/> timeout discriminator, so every other provider failure
/// (auth, network, 4xx/5xx, deserialization) escaped to the coordinator's generic handler with no
/// vocabulary for WHY. These tests pin the new reason, its non-mutation of history, its
/// participation in the circuit breaker, the verbatim exception message in the log, and - most
/// importantly - that the new broad catch did NOT cannibalise the timeout discriminator or
/// swallow caller cancellation.
/// </summary>
public sealed class CompactionSummarizationFailedTests
{
    private const string ProviderErrorMessage = "provider rejected the API key (HTTP 401 unauthorized)";

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

    private static CompactionOptions SummarisingOptions(int timeoutSeconds = 90) => new()
    {
        TimeoutSeconds = timeoutSeconds,
        ContextWindowTokens = 100,
        TokenThresholdRatio = 0.01,
        PreservedTurns = 1,
        SummarizationModel = TestModel.Id
    };

    // ── AC1: the reason is actually produced ─────────────────────────────────

    [Fact]
    public async Task CompactAsync_SummarizationThrows_StampsSummarizationFailedReason()
    {
        var session = CreateLargeSession(100);
        var compactor = CreateThrowingCompactor(new InvalidOperationException(ProviderErrorMessage), out _);

        var result = await compactor.CompactAsync(session, SummarisingOptions());

        result.Succeeded.ShouldBeFalse();
        result.SkipReason.ShouldBe(CompactionSkipReason.SummarizationFailed);
    }

    // ── AC2: history is not mutated ──────────────────────────────────────────

    [Fact]
    public async Task CompactAsync_SummarizationThrows_LeavesHistoryUnchanged()
    {
        var session = CreateLargeSession(100);
        var before = session.GetHistorySnapshot().Select(entry => entry.Content).ToList();
        var compactor = CreateThrowingCompactor(new InvalidOperationException(ProviderErrorMessage), out _);

        var result = await compactor.CompactAsync(session, SummarisingOptions());

        result.SkipReason.ShouldBe(CompactionSkipReason.SummarizationFailed);
        result.CompactedHistory.ShouldBeNull();

        var after = session.GetHistorySnapshot().Select(entry => entry.Content).ToList();
        after.Count.ShouldBe(before.Count);
        after.ShouldBe(before);
    }

    // ── AC3: participates in the existing circuit breaker ────────────────────

    [Fact]
    public async Task CompactAsync_RepeatedSummarizationFailures_OpenCircuitBreaker()
    {
        var session = CreateLargeSession(100);
        var compactor = CreateThrowingCompactor(new InvalidOperationException(ProviderErrorMessage), out _);
        var options = SummarisingOptions();

        for (var i = 0; i < LlmSessionCompactor.MaxConsecutiveFailures; i++)
        {
            var attempt = await compactor.CompactAsync(session, options);
            attempt.SkipReason.ShouldBe(CompactionSkipReason.SummarizationFailed);
        }

        // The existing breaker (from #2460/#2465) must now short-circuit - proving the new
        // failure path reused RecordFailure rather than inventing a second counter.
        var result = await compactor.CompactAsync(session, options);
        result.Succeeded.ShouldBeFalse();
        result.SkipReason.ShouldBe(CompactionSkipReason.CircuitBreakerOpen);
    }

    // ── AC4: the underlying message survives verbatim, no size narrative ─────

    [Fact]
    public async Task CompactAsync_SummarizationThrows_LogsUnderlyingMessageVerbatim()
    {
        var session = CreateLargeSession(100);
        var thrown = new InvalidOperationException(ProviderErrorMessage);
        var compactor = CreateThrowingCompactor(thrown, out var logger);

        var result = await compactor.CompactAsync(session, SummarisingOptions());
        result.SkipReason.ShouldBe(CompactionSkipReason.SummarizationFailed);

        var warning = logger.Entries
            .Where(entry => entry.Level == LogLevel.Warning)
            .FirstOrDefault(entry => entry.Message.Contains(ProviderErrorMessage, StringComparison.Ordinal));

        warning.ShouldNotBeNull();
        warning.Exception.ShouldBeSameAs(thrown);

        // The upstream defect being fixed is paraphrasing a provider failure into a
        // size/threshold narrative. The failure log must not do that.
        warning.Message.Contains("size", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        warning.Message.Contains("threshold", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        warning.Message.Contains("token", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    // ── AC5: DISCRIMINATION - cancellation-shaped failures are NOT SummarizationFailed ──

    /// <summary>
    /// A provider stall (per-attempt timeout) must NOT be reclassified as
    /// <see cref="CompactionSkipReason.SummarizationFailed"/> by the new broad catch.
    /// NOTE (verified at HEAD, unchanged by this PR): a hung single-candidate provider is
    /// absorbed by the pre-existing per-attempt <c>OperationCanceledException</c> handler inside
    /// <c>TryCallModelAsync</c>, which returns an empty result, so the observable reason is
    /// <see cref="CompactionSkipReason.EmptySummary"/>; the outer
    /// <see cref="CompactionSkipReason.SummarizationTimeout"/> branch is only reachable when an
    /// OCE escapes the fallback chain. Either way, the discrimination this test pins is that the
    /// new catch does not cannibalise a cancellation-shaped failure.
    /// </summary>
    [Fact]
    public async Task CompactAsync_ProviderStall_IsNotReclassifiedAsSummarizationFailed()
    {
        var session = CreateLargeSession(100);
        var compactor = CreateHungCompactor();

        var result = await compactor.CompactAsync(session, SummarisingOptions(timeoutSeconds: 1));

        result.Succeeded.ShouldBeFalse();
        result.SkipReason.ShouldNotBe(CompactionSkipReason.SummarizationFailed);
        result.SkipReason.ShouldBe(CompactionSkipReason.EmptySummary);
    }

    /// <summary>
    /// Direct proof of catch ORDERING: the new <c>catch (Exception)</c> carries a
    /// <c>when (ex is not OperationCanceledException)</c> filter and is declared AFTER the
    /// OperationCanceledException timeout discriminator, so no OCE can ever reach it. If the
    /// ordering or the filter were wrong, this OCE would be stamped SummarizationFailed.
    /// </summary>
    [Fact]
    public async Task CompactAsync_OperationCanceledFromProvider_NeverYieldsSummarizationFailed()
    {
        var session = CreateLargeSession(100);
        var compactor = CreateThrowingCompactor(new OperationCanceledException("provider aborted"), out _);

        var result = await compactor.CompactAsync(session, SummarisingOptions());

        result.Succeeded.ShouldBeFalse();
        result.SkipReason.ShouldNotBe(CompactionSkipReason.SummarizationFailed);
    }

    // ── AC6: caller cancellation still propagates, not swallowed ─────────────

    [Fact]
    public async Task CompactAsync_CallerCancellation_PropagatesAndIsNotSwallowed()
    {
        var session = CreateLargeSession(100);
        var compactor = CreateHungCompactor();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // OperationCanceledException derives from Exception, so a naive catch (Exception)
        // would swallow caller cancellation and return Skipped instead of throwing.
        await Should.ThrowAsync<OperationCanceledException>(
            () => compactor.CompactAsync(session, SummarisingOptions(timeoutSeconds: 300), cts.Token));
    }

    [Fact]
    public async Task CompactAsync_PreCancelledToken_PropagatesEvenWhenProviderWouldThrow()
    {
        var session = CreateLargeSession(100);
        var compactor = CreateThrowingCompactor(new InvalidOperationException(ProviderErrorMessage), out _);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => compactor.CompactAsync(session, SummarisingOptions(), cts.Token));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

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

    private static LlmSessionCompactor CreateThrowingCompactor(Exception toThrow, out ListLogger<LlmSessionCompactor> logger)
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
            .Throws(toThrow);

        providers.Register(provider.Object);

        logger = new ListLogger<LlmSessionCompactor>();
        return new LlmSessionCompactor(new LlmClient(providers, models), logger);
    }

    private static LlmSessionCompactor CreateHungCompactor()
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
            .Returns(() => new LlmStream());

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
