using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// #3362: a transcript/history READ failure was indistinguishable from a legitimately empty
/// history. <c>LlmSessionCompactor</c> stamped <see cref="CompactionSkipReason.EmptyHistory"/>
/// whenever the snapshot came back with zero entries — including when it came back empty because
/// loading it had failed — and <c>SessionCompactionCoordinator</c> hard-coded
/// <c>failureReason</c> to "the summarization model returned an empty response" for EVERY
/// non-success, so an I/O, permissions or deserialization fault was reported to the operator as a
/// summarization-model problem that did not exist.
/// <para>
/// These tests pin the new discriminator and, just as importantly, pin that it did NOT cannibalise
/// the genuinely-empty, timeout, cancellation or summarization-failure branches.
/// </para>
/// </summary>
public sealed class CompactionHistoryReadFailureTests
{
    private const string SummarizationModelText = "summarization model";
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

    // ── AC1/AC3: a throwing history source produces the read-failure discriminator ──

    [Fact]
    public async Task CompactAsync_HistorySourceThrows_StampsHistoryReadFailed_NotEmptyHistory()
    {
        var session = CreateSession(("user", "u1"), ("assistant", "a1"));
        var compactor = CreateCompactorWithHistorySource(
            _ => throw new IOException("transcript store unavailable"));

        var result = await compactor.CompactAsync(session, DefaultOptions());

        result.Succeeded.ShouldBeFalse();
        result.SkipReason.ShouldBe(CompactionSkipReason.HistoryReadFailed);
        result.SkipReason.ShouldNotBe(CompactionSkipReason.EmptyHistory);
    }

    [Fact]
    public async Task CompactAsync_HistorySourceThrows_LeavesHistoryUnchanged()
    {
        var session = CreateSession(("user", "u1"), ("assistant", "a1"));
        var before = session.GetHistorySnapshot().Select(entry => entry.Content).ToList();
        var compactor = CreateCompactorWithHistorySource(
            _ => throw new IOException("transcript store unavailable"));

        var result = await compactor.CompactAsync(session, DefaultOptions());

        result.CompactedHistory.ShouldBeNull();
        session.GetHistorySnapshot().Select(entry => entry.Content).ToList().ShouldBe(before);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("unauthorized")]
    [InlineData("json")]
    public async Task CompactAsync_ReadShapedFailures_AllStampHistoryReadFailed(string kind)
    {
        var session = CreateSession(("user", "u1"), ("assistant", "a1"));
        Exception thrown = kind switch
        {
            "io" => new IOException("disk error"),
            "unauthorized" => new UnauthorizedAccessException("permission denied"),
            _ => new System.Text.Json.JsonException("malformed transcript"),
        };

        var compactor = CreateCompactorWithHistorySource(_ => throw thrown);

        var result = await compactor.CompactAsync(session, DefaultOptions());

        result.SkipReason.ShouldBe(CompactionSkipReason.HistoryReadFailed);
    }

    /// <summary>
    /// AC3, coordinator half: the operator-visible message for a read failure must NOT accuse the
    /// summarization model, and must carry the underlying exception type so the true fault
    /// (store corruption, permissions, disk) is diagnosable.
    /// </summary>
    [Fact]
    public async Task CoordinatorCompactAsync_HistoryReadFailure_ReportsReadFailure_WithoutBlamingTheModel()
    {
        var coordinator = CreateCoordinator(
            CompactionResult.Skipped(skipReason: CompactionSkipReason.HistoryReadFailed));
        var session = CreateSession(("user", "u1"));

        var outcome = await coordinator.CompactAsync(TestAgent, session, CancellationToken.None);

        outcome.Applied.ShouldBeFalse();
        outcome.SkipReason.ShouldBe(CompactionSkipReason.HistoryReadFailed);
        outcome.FailureReason.ShouldNotBeNull();
        outcome.FailureReason.ShouldNotContain(SummarizationModelText, Case.Insensitive);
        // The message must name what actually broke, not a generic abort.
        outcome.FailureReason.ShouldContain("history", Case.Insensitive);
    }

    /// <summary>
    /// The coordinator's own broad catch must classify a read-shaped exception escaping the
    /// compactor as a read failure, and propagate the exception TYPE for diagnosis.
    /// </summary>
    [Fact]
    public async Task CoordinatorCompactAsync_CompactorThrowsReadShapedException_ClassifiesAsHistoryReadFailed()
    {
        var compactor = new Mock<ISessionCompactor>();
        compactor
            .Setup(c => c.CompactAsync(It.IsAny<GatewaySession>(), It.IsAny<CompactionOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("transcript store unavailable"));

        var coordinator = CreateCoordinator(compactor);
        var session = CreateSession(("user", "u1"));

        var outcome = await coordinator.CompactAsync(TestAgent, session, CancellationToken.None);

        outcome.SkipReason.ShouldBe(CompactionSkipReason.HistoryReadFailed);
        outcome.FailureExceptionType.ShouldBe(nameof(IOException));
        outcome.FailureReason.ShouldNotBeNull();
        outcome.FailureReason.ShouldNotContain(SummarizationModelText, Case.Insensitive);
    }

    // ── AC2: failureReason is DERIVED, and the empty-response text is reserved ──

    [Fact]
    public async Task CoordinatorCompactAsync_EmptySummary_StillReportsTheEmptyResponseText()
    {
        var coordinator = CreateCoordinator(
            CompactionResult.Skipped(skipReason: CompactionSkipReason.EmptySummary));
        var session = CreateSession(("user", "u1"));

        var outcome = await coordinator.CompactAsync(TestAgent, session, CancellationToken.None);

        outcome.SkipReason.ShouldBe(CompactionSkipReason.EmptySummary);
        outcome.FailureReason.ShouldNotBeNull();
        outcome.FailureReason.ShouldContain(SummarizationModelText, Case.Insensitive);
        outcome.FailureReason.ShouldContain("empty response", Case.Insensitive);
    }

    /// <summary>
    /// Non-vacuity guard for AC2: the empty-response text must be emitted for EXACTLY ONE reason.
    /// A regression that reinstated the hard-coded string would make several of these fail.
    /// </summary>
    [Theory]
    [InlineData("HistoryReadFailed")]
    [InlineData("EmptyHistory")]
    [InlineData("NoSummarizableTurns")]
    [InlineData("SummarizationTimeout")]
    [InlineData("SummarizationFailed")]
    [InlineData("CircuitBreakerOpen")]
    public async Task CoordinatorCompactAsync_NonEmptySummaryReasons_NeverMentionTheSummarizationModelEmptyResponse(string wireValue)
    {
        var coordinator = CreateCoordinator(
            CompactionResult.Skipped(skipReason: CompactionSkipReason.FromString(wireValue)));
        var session = CreateSession(("user", "u1"));

        var outcome = await coordinator.CompactAsync(TestAgent, session, CancellationToken.None);

        outcome.SkipReason!.Value.ShouldBe(wireValue);
        outcome.FailureReason.ShouldNotBeNull();
        outcome.FailureReason.ShouldNotContain("empty response", Case.Insensitive);
    }

    // ── AC4: no conflation in the other direction ──

    [Fact]
    public async Task CompactAsync_GenuinelyEmptyHistory_StillStampsEmptyHistory()
    {
        var session = CreateSession();
        var compactor = CreateCompactor();

        var result = await compactor.CompactAsync(session, DefaultOptions());

        result.Succeeded.ShouldBeFalse();
        result.SkipReason.ShouldBe(CompactionSkipReason.EmptyHistory);
        result.SkipReason.ShouldNotBe(CompactionSkipReason.HistoryReadFailed);
    }

    /// <summary>
    /// A history source that legitimately RETURNS an empty snapshot (rather than throwing) is
    /// still EmptyHistory — proving the discriminator keys on the failure, not on the count.
    /// </summary>
    [Fact]
    public async Task CompactAsync_HistorySourceReturnsEmptySnapshot_StillStampsEmptyHistory()
    {
        var session = CreateSession(("user", "u1"), ("assistant", "a1"));
        var compactor = CreateCompactorWithHistorySource(_ => new HistorySnapshot([], 0, 0));

        var result = await compactor.CompactAsync(session, DefaultOptions());

        result.SkipReason.ShouldBe(CompactionSkipReason.EmptyHistory);
    }

    [Fact]
    public async Task CoordinatorCompactAsync_EmptyHistory_ReportsEmptyHistory_NotAReadFailure()
    {
        var coordinator = CreateCoordinator(
            CompactionResult.Skipped(skipReason: CompactionSkipReason.EmptyHistory));
        var session = CreateSession(("user", "u1"));

        var outcome = await coordinator.CompactAsync(TestAgent, session, CancellationToken.None);

        outcome.SkipReason.ShouldBe(CompactionSkipReason.EmptyHistory);
        outcome.FailureExceptionType.ShouldBeNull();
        outcome.FailureReason.ShouldNotBeNull();
        outcome.FailureReason.ShouldNotContain(SummarizationModelText, Case.Insensitive);
    }

    // ── AC5: the cancellation/summarization discriminators are NOT cannibalised ──

    [Fact]
    public async Task CoordinatorCompactAsync_CompactorThrowsProviderException_StaysSummarizationFailed()
    {
        var compactor = new Mock<ISessionCompactor>();
        compactor
            .Setup(c => c.CompactAsync(It.IsAny<GatewaySession>(), It.IsAny<CompactionOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider exploded"));

        var coordinator = CreateCoordinator(compactor);
        var session = CreateSession(("user", "u1"));

        var outcome = await coordinator.CompactAsync(TestAgent, session, CancellationToken.None);

        outcome.SkipReason.ShouldBe(CompactionSkipReason.SummarizationFailed);
        outcome.SkipReason.ShouldNotBe(CompactionSkipReason.HistoryReadFailed);
    }

    [Fact]
    public async Task CompactAsync_HistorySourceThrowsOperationCanceled_PropagatesRatherThanBecomingAReadFailure()
    {
        var session = CreateSession(("user", "u1"), ("assistant", "a1"));
        var compactor = CreateCompactorWithHistorySource(
            _ => throw new OperationCanceledException("caller went away"));

        // OperationCanceledException derives from Exception; a naive read-failure catch would
        // swallow caller cancellation and report HistoryReadFailed instead of propagating.
        await Should.ThrowAsync<OperationCanceledException>(
            () => compactor.CompactAsync(session, DefaultOptions()));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static CompactionOptions DefaultOptions() => new()
    {
        PreservedTurns = 1,
        ContextWindowTokens = 100,
        TokenThresholdRatio = 0.01,
        SummarizationModel = TestModel.Id
    };

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

    private static LlmSessionCompactor CreateCompactor(
        Func<GatewaySession, HistorySnapshot>? historySource = null)
    {
        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();
        models.Register(TestModel.Provider, TestModel);

        return new LlmSessionCompactor(
            new LlmClient(providers, models),
            NullLogger<LlmSessionCompactor>.Instance,
            historySnapshotReader: historySource);
    }

    private static LlmSessionCompactor CreateCompactorWithHistorySource(
        Func<GatewaySession, HistorySnapshot> historySource) => CreateCompactor(historySource);

    private static SessionCompactionCoordinator CreateCoordinator(CompactionResult result)
    {
        var compactor = new Mock<ISessionCompactor>();
        compactor
            .Setup(c => c.CompactAsync(It.IsAny<GatewaySession>(), It.IsAny<CompactionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return CreateCoordinator(compactor);
    }

    private static SessionCompactionCoordinator CreateCoordinator(Mock<ISessionCompactor> compactor)
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
            NullLogger<SessionCompactionCoordinator>.Instance);
    }
}
