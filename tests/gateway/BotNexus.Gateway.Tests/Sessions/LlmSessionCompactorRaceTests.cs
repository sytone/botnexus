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
/// Race-condition regression suite for #532. The pre-fix behaviour was that
/// <see cref="LlmSessionCompactor.CompactAsync"/> snapshotted <c>session.History</c>
/// by reference, awaited the LLM summary call, then the caller did
/// <c>session.ReplaceHistory(result.CompactedHistory)</c> under the runtime lock —
/// silently dropping any <c>AddEntry</c> calls that landed during the LLM window.
///
/// The fix uses optimistic versioning with split addition/destructive counters,
/// captured atomically at snapshot time and verified at apply time:
/// <list type="bullet">
///   <item><term>Applied</term><description>no concurrent mutation — straight replace</description></item>
///   <item><term>Rebased</term><description>only additions happened — concurrent tail appended after compacted output</description></item>
///   <item><term>Aborted</term><description>destructive change happened — apply refused, history unchanged</description></item>
/// </list>
/// </summary>
public sealed class LlmSessionCompactorRaceTests
{
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

    private static readonly CompactionOptions DefaultOptions = new()
    {
        PreservedTurns = 1,
        SummarizationModel = TestModel.Id
    };

    // ─── Baseline (no concurrency) ──────────────────────────────────────────────

    [Fact]
    public async Task NoConcurrentMutation_ApplyReturnsApplied_HistoryReplacedExactly()
    {
        var session = CreateSession(("user", "u1"), ("assistant", "a1"), ("user", "u2"));
        var harness = new SlowLlmHarness(summary: "summary-1");
        var compactor = harness.CreateCompactor();

        var resultTask = compactor.CompactAsync(session, DefaultOptions);
        harness.WaitUntilEntered();
        harness.Release();
        var result = await resultTask;

        result.Succeeded.ShouldBeTrue();
        var outcome = session.TryApplyCompactionResult(result);
        outcome.ShouldBe(HistoryReplaceOutcome.Applied);

        session.GetHistorySnapshot()
            .Select(e => e.IsCompactionSummary ? e.Content.Replace(LlmSessionCompactor.SummaryPrefix + "\n", "") : e.Content)
            .ToList()
            .ShouldBe(["u1", "a1", "summary-1", "u2"], ignoreOrder: false);
    }

    // ─── Rebase scenarios — only additions happened ─────────────────────────────

    /// <summary>
    /// The core #532 regression: a SignalR client adds an inbound while the
    /// summariser is running. The new entry must survive the apply.
    /// </summary>
    [Fact]
    public async Task SingleAddEntry_DuringCompaction_IsPreservedAfterRebase()
    {
        var session = CreateSession(("user", "u1"), ("assistant", "a1"), ("user", "u2"));
        var harness = new SlowLlmHarness(summary: "summary-1");
        var compactor = harness.CreateCompactor();

        var resultTask = compactor.CompactAsync(session, DefaultOptions);
        harness.WaitUntilEntered();
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "raced-in" });
        harness.Release();
        var result = await resultTask;

        var outcome = session.TryApplyCompactionResult(result);
        outcome.ShouldBe(HistoryReplaceOutcome.Rebased);

        session.GetHistorySnapshot()
            .Select(e => e.IsCompactionSummary ? e.Content.Replace(LlmSessionCompactor.SummaryPrefix + "\n", "") : e.Content)
            .ToList()
            .ShouldBe(["u1", "a1", "summary-1", "u2", "raced-in"], ignoreOrder: false);
    }

    [Fact]
    public async Task MultipleAddEntries_DuringCompaction_AllPreservedInOrder()
    {
        var session = CreateSession(("user", "u1"), ("assistant", "a1"), ("user", "u2"));
        var harness = new SlowLlmHarness(summary: "summary-1");
        var compactor = harness.CreateCompactor();

        var resultTask = compactor.CompactAsync(session, DefaultOptions);
        harness.WaitUntilEntered();
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "r1" });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "r2" });
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "r3" });
        harness.Release();
        var result = await resultTask;

        var outcome = session.TryApplyCompactionResult(result);
        outcome.ShouldBe(HistoryReplaceOutcome.Rebased);

        session.GetHistorySnapshot()
            .Select(e => e.IsCompactionSummary ? e.Content.Replace(LlmSessionCompactor.SummaryPrefix + "\n", "") : e.Content)
            .ToList()
            .ShouldBe(["u1", "a1", "summary-1", "u2", "r1", "r2", "r3"], ignoreOrder: false);
    }

    [Fact]
    public async Task BatchAddEntries_DuringCompaction_AllPreservedInOrder()
    {
        var session = CreateSession(("user", "u1"), ("assistant", "a1"), ("user", "u2"));
        var harness = new SlowLlmHarness(summary: "summary-1");
        var compactor = harness.CreateCompactor();

        var resultTask = compactor.CompactAsync(session, DefaultOptions);
        harness.WaitUntilEntered();
        session.AddEntries(
        [
            new SessionEntry { Role = MessageRole.User, Content = "batch-1" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "batch-2" }
        ]);
        harness.Release();
        var result = await resultTask;

        var outcome = session.TryApplyCompactionResult(result);
        outcome.ShouldBe(HistoryReplaceOutcome.Rebased);

        session.GetHistorySnapshot()
            .Select(e => e.IsCompactionSummary ? e.Content.Replace(LlmSessionCompactor.SummaryPrefix + "\n", "") : e.Content)
            .ToList()
            .ShouldBe(["u1", "a1", "summary-1", "u2", "batch-1", "batch-2"], ignoreOrder: false);
    }

    // ─── Abort scenarios — destructive change happened ──────────────────────────

    /// <summary>
    /// Crash sentinel removal mid-compaction. If we naively rebased we would
    /// re-introduce the removed sentinel (which the compacted history carried
    /// through). Apply must abort and leave the live history alone.
    /// </summary>
    [Fact]
    public async Task RemoveCrashSentinels_DuringCompaction_ForcesAbort_HistoryUnchanged()
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(Guid.NewGuid().ToString("N")),
            AgentId = AgentId.From("agent")
        };
        session.AddEntries(
        [
            new SessionEntry { Role = MessageRole.User, Content = "u1" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a1" },
            new SessionEntry { Role = MessageRole.User, Content = "u2" },
            new SessionEntry
            {
                Role = MessageRole.System,
                Content = "[agent turn in progress — gateway restarted if visible]",
                IsCrashSentinel = true
            }
        ]);

        var harness = new SlowLlmHarness(summary: "summary-1");
        var compactor = harness.CreateCompactor();

        var resultTask = compactor.CompactAsync(session, DefaultOptions);
        harness.WaitUntilEntered();
        session.RemoveCrashSentinels();
        harness.Release();
        var result = await resultTask;

        var outcome = session.TryApplyCompactionResult(result);
        outcome.ShouldBe(HistoryReplaceOutcome.Aborted);

        session.GetHistorySnapshot()
            .Select(e => e.IsCompactionSummary ? e.Content.Replace(LlmSessionCompactor.SummaryPrefix + "\n", "") : e.Content)
            .ToList()
            .ShouldBe(["u1", "a1", "u2"], ignoreOrder: false);
        session.GetHistorySnapshot().ShouldNotContain(e => e.IsCrashSentinel);
    }

    /// <summary>
    /// A no-op crash-sentinel scrub (nothing to remove) must not bump the
    /// destructive version; the apply should still succeed as the fast path.
    /// </summary>
    [Fact]
    public async Task RemoveCrashSentinels_NoOp_DoesNotAbort_AppliedOutcome()
    {
        var session = CreateSession(("user", "u1"), ("assistant", "a1"), ("user", "u2"));
        var harness = new SlowLlmHarness(summary: "summary-1");
        var compactor = harness.CreateCompactor();

        var resultTask = compactor.CompactAsync(session, DefaultOptions);
        harness.WaitUntilEntered();
        session.RemoveCrashSentinels();
        harness.Release();
        var result = await resultTask;

        var outcome = session.TryApplyCompactionResult(result);
        outcome.ShouldBe(HistoryReplaceOutcome.Applied);

        session.GetHistorySnapshot()
            .Select(e => e.IsCompactionSummary ? e.Content.Replace(LlmSessionCompactor.SummaryPrefix + "\n", "") : e.Content)
            .ToList()
            .ShouldBe(["u1", "a1", "summary-1", "u2"], ignoreOrder: false);
    }

    /// <summary>
    /// Heartbeat-restore path: an unrelated <c>ReplaceHistory</c> happens
    /// concurrently. Apply must abort to avoid clobbering the restore.
    /// </summary>
    [Fact]
    public async Task ReplaceHistory_DuringCompaction_ForcesAbort_HistoryUnchanged()
    {
        var session = CreateSession(("user", "u1"), ("assistant", "a1"), ("user", "u2"));
        var harness = new SlowLlmHarness(summary: "summary-1");
        var compactor = harness.CreateCompactor();

        var replacement = new List<SessionEntry>
        {
            new() { Role = MessageRole.User, Content = "restored-1" },
            new() { Role = MessageRole.Assistant, Content = "restored-2" }
        };

        var resultTask = compactor.CompactAsync(session, DefaultOptions);
        harness.WaitUntilEntered();
        session.ReplaceHistory(replacement);
        harness.Release();
        var result = await resultTask;

        var outcome = session.TryApplyCompactionResult(result);
        outcome.ShouldBe(HistoryReplaceOutcome.Aborted);

        session.GetHistorySnapshot()
            .Select(e => e.IsCompactionSummary ? e.Content.Replace(LlmSessionCompactor.SummaryPrefix + "\n", "") : e.Content)
            .ToList()
            .ShouldBe(["restored-1", "restored-2"], ignoreOrder: false);
    }

    /// <summary>
    /// Two overlapping compactions from the same snapshot version. First wins
    /// (Applied), second sees the bumped destructive version and aborts —
    /// re-applying a stale compaction would clobber the first one's changes.
    /// </summary>
    [Fact]
    public async Task TwoOverlappingCompactions_FirstApplies_SecondAborts()
    {
        var session = CreateSession(("user", "u1"), ("assistant", "a1"), ("user", "u2"));
        var harness1 = new SlowLlmHarness(summary: "summary-A");
        var harness2 = new SlowLlmHarness(summary: "summary-B");
        var compactor1 = harness1.CreateCompactor();
        var compactor2 = harness2.CreateCompactor();

        var task1 = compactor1.CompactAsync(session, DefaultOptions);
        harness1.WaitUntilEntered();
        var task2 = compactor2.CompactAsync(session, DefaultOptions);
        harness2.WaitUntilEntered();

        harness1.Release();
        var result1 = await task1;
        harness2.Release();
        var result2 = await task2;

        var outcome1 = session.TryApplyCompactionResult(result1);
        var outcome2 = session.TryApplyCompactionResult(result2);
        outcome1.ShouldBe(HistoryReplaceOutcome.Applied);
        outcome2.ShouldBe(HistoryReplaceOutcome.Aborted);

        // First compaction wins; second is stale and discarded.
        session.GetHistorySnapshot()
            .Select(e => e.IsCompactionSummary ? e.Content.Replace(LlmSessionCompactor.SummaryPrefix + "\n", "") : e.Content)
            .ToList()
            .ShouldBe(["u1", "a1", "summary-A", "u2"], ignoreOrder: false);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static GatewaySession CreateSession(params (string role, string content)[] entries)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(Guid.NewGuid().ToString("N")),
            AgentId = AgentId.From("agent")
        };
        session.AddEntries(entries.Select(e => new SessionEntry { Role = e.role, Content = e.content }));
        return session;
    }

    /// <summary>
    /// Test harness that wraps an <see cref="LlmClient"/> whose stream signals
    /// when the compactor enters the LLM call and blocks until the test
    /// explicitly releases it. Lets tests interleave concurrent session
    /// mutations precisely without sleeps or polling.
    /// </summary>
    private sealed class SlowLlmHarness
    {
        private readonly string _summary;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SlowLlmHarness(string summary)
        {
            _summary = summary;
        }

        public LlmSessionCompactor CreateCompactor()
        {
            var providers = new ApiProviderRegistry();
            var models = new ModelRegistry();
            models.Register(TestModel.Provider, TestModel);

            var provider = new Mock<IApiProvider>();
            provider.SetupGet(p => p.Api).Returns(TestModel.Api);
            provider
                .Setup(p => p.StreamSimple(
                    It.IsAny<LlmModel>(),
                    It.IsAny<Context>(),
                    It.IsAny<SimpleStreamOptions?>()))
                .Returns(() =>
                {
                    var stream = new LlmStream();
                    _entered.TrySetResult();
                    _ = Task.Run(async () =>
                    {
                        await _release.Task.ConfigureAwait(false);
                        var message = new AssistantMessage(
                            Content: [new TextContent(_summary)],
                            Api: TestModel.Api,
                            Provider: TestModel.Provider,
                            ModelId: TestModel.Id,
                            Usage: Usage.Empty(),
                            StopReason: StopReason.Stop,
                            ErrorMessage: null,
                            ResponseId: null,
                            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        stream.Push(new DoneEvent(StopReason.Stop, message));
                        stream.End(message);
                    });
                    return stream;
                });

            providers.Register(provider.Object);
            var client = new LlmClient(providers, models);
            return new LlmSessionCompactor(client, NullLogger<LlmSessionCompactor>.Instance);
        }

        public void WaitUntilEntered()
        {
            if (!_entered.Task.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Compactor did not enter the LLM call within 5s.");
        }

        public void Release() => _release.TrySetResult();
    }
}
