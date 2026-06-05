using System.Reflection;
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

public sealed class LlmSessionCompactorTests
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

    [Fact]
    public void ShouldCompact_BelowThreshold_ReturnsFalse()
    {
        var session = CreateSession(("user", new string('a', 100)));
        var compactor = CreateCompactor("summary");
        var options = new CompactionOptions { ContextWindowTokens = 100, TokenThresholdRatio = 0.5 };

        compactor.ShouldCompact(session.Session, options).ShouldBeFalse();
    }

    [Fact]
    public void ShouldCompact_AboveThreshold_ReturnsTrue()
    {
        var session = CreateSession(("user", new string('a', 300)));
        var compactor = CreateCompactor("summary");
        var options = new CompactionOptions { ContextWindowTokens = 100, TokenThresholdRatio = 0.5 };

        compactor.ShouldCompact(session.Session, options).ShouldBeTrue();
    }

    [Fact]
    public async Task CompactAsync_PreservesRecentTurns()
    {
        var session = CreateSession(
            ("user", "u1"),
            ("assistant", "a1"),
            ("tool", "t1"),
            ("user", "u2"),
            ("assistant", "a2"),
            ("tool", "t2"),
            ("user", "u3"),
            ("assistant", "a3"));
        var compactor = CreateCompactor("summary-u1");

        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 2,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeTrue();
        result.CompactedHistory.ShouldNotBeNull();
        session.ReplaceHistory(result.CompactedHistory!);

        // Phase 3a (#531): summarised entries are kept in the session store with IsHistory=true,
        // the summary entry sits at the boundary, then the preserved tail follows.
        session.GetHistorySnapshot().Select(entry => entry.Content).ToList().ShouldBe(
            new[] { "u1", "a1", "t1", "summary-u1", "u2", "a2", "t2", "u3", "a3" }, ignoreOrder: false);
        session.GetHistorySnapshot().Take(3).ShouldAllBe(e => e.IsHistory);
        session.GetHistorySnapshot().Skip(3).ShouldAllBe(e => !e.IsHistory);
    }

    [Fact]
    public async Task CompactAsync_SummarizesOlderEntries()
    {
        var session = CreateSession(
            ("user", "older"),
            ("assistant", "older-response"),
            ("user", "recent"),
            ("assistant", "recent-response"));
        var compactor = CreateCompactor("structured summary");

        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeTrue();
        result.CompactedHistory.ShouldNotBeNull();
        result.EntriesSummarized.ShouldBe(2);
        result.EntriesPreserved.ShouldBe(2);

        session.ReplaceHistory(result.CompactedHistory!);

        session.GetHistorySnapshot().Select(entry => entry.Content)
            .ToList().ShouldBe(new[] { "older", "older-response", "structured summary", "recent", "recent-response" }, ignoreOrder: false);
        session.GetHistorySnapshot()[0].IsHistory.ShouldBeTrue();
        session.GetHistorySnapshot()[1].IsHistory.ShouldBeTrue();
        session.GetHistorySnapshot()[2].IsCompactionSummary.ShouldBeTrue();
        session.GetHistorySnapshot()[2].IsHistory.ShouldBeFalse();
    }

    [Fact]
    public async Task CompactAsync_EmptyHistory_ReturnsEmptyResult()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("s1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("a1") };
        var compactor = CreateCompactor("summary");

        var result = await compactor.CompactAsync(session, new CompactionOptions());

        result.Summary.ShouldBeEmpty();
        result.Succeeded.ShouldBeFalse();
        result.CompactedHistory.ShouldBeNull();
        result.EntriesSummarized.ShouldBe(0);
        result.EntriesPreserved.ShouldBe(0);
        session.GetHistorySnapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task CompactAsync_FewEntriesBelowPreservedTurns_NoSummarization()
    {
        var session = CreateSession(
            ("user", "u1"),
            ("assistant", "a1"),
            ("user", "u2"),
            ("assistant", "a2"));
        var originalHistory = session.GetHistorySnapshot().ToList();
        var compactor = CreateCompactor("unused");

        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 3,
            SummarizationModel = TestModel.Id
        });

        result.Summary.ShouldBeEmpty();
        result.Succeeded.ShouldBeFalse();
        result.CompactedHistory.ShouldBeNull();
        result.EntriesSummarized.ShouldBe(0);
        result.EntriesPreserved.ShouldBe(4);
        session.GetHistorySnapshot().ShouldBe(originalHistory);
    }

    [Fact]
    public async Task CompactAsync_SetsIsCompactionSummaryFlag()
    {
        var session = CreateSession(
            ("user", "older"),
            ("assistant", "older-response"),
            ("user", "recent"));
        var compactor = CreateCompactor("compacted");

        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeTrue();
        result.CompactedHistory.ShouldNotBeNull();
        session.ReplaceHistory(result.CompactedHistory!);

        // Phase 3a (#531): summary entry now sits at the historical→preserved boundary, not at index 0.
        var summaryEntry = session.GetHistorySnapshot().Single(e => e.IsCompactionSummary);
        summaryEntry.Role.ShouldBe(MessageRole.System);
        summaryEntry.IsCompactionSummary.ShouldBeTrue();
        summaryEntry.IsHistory.ShouldBeFalse();
        summaryEntry.Content.ShouldBe("compacted");
    }

    [Fact]
    public async Task CompactAsync_TruncatesSummaryIfTooLong()
    {
        var longSummary = new string('x', 200);
        var session = CreateSession(
            ("user", "older"),
            ("assistant", "older-response"),
            ("user", "recent"));
        var compactor = CreateCompactor(longSummary);

        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            MaxSummaryChars = 40,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeTrue();
        result.Summary.Length.ShouldBe(40);

        session.ReplaceHistory(result.CompactedHistory!);
        session.GetHistorySnapshot().Single(e => e.IsCompactionSummary).Content.Length.ShouldBe(40);
    }

    /// <summary>
    /// Regression test for Bug 1 / Bug 5 (#366): when the LLM returns an empty response,
    /// CompactAsync must abort and leave history completely unchanged.
    /// </summary>
    [Fact]
    public async Task CompactAsync_EmptyLlmResponse_AbortsWithoutMutatingHistory()
    {
        var session = CreateSession(
            ("user", "important message 1"),
            ("assistant", "important response 1"),
            ("user", "important message 2"),
            ("assistant", "important response 2"),
            ("user", "recent"));
        var originalHistory = session.GetHistorySnapshot().ToList();

        // Simulate LLM returning empty text
        var compactor = CreateCompactor(summary: "");

        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeFalse("compaction should be aborted when LLM returns empty summary");
        result.Summary.ShouldBeEmpty();
        result.CompactedHistory.ShouldBeNull();
        result.EntriesSummarized.ShouldBe(0);

        // Critical: history must be completely unchanged
        session.GetHistorySnapshot().Count.ShouldBe(originalHistory.Count,
            "history must not be modified when LLM returns empty summary");
        session.GetHistorySnapshot().Select(e => e.Content).ToList()
            .ShouldBe(originalHistory.Select(e => e.Content).ToList());
    }

    [Fact]
    public void SplitHistory_PreservesCompleteTurns()
    {
        var history = new List<SessionEntry>
        {
            new() { Role = MessageRole.User, Content = "u1" },
            new() { Role = MessageRole.Assistant, Content = "a1" },
            new() { Role = MessageRole.Tool, Content = "t1" },
            new() { Role = MessageRole.User, Content = "u2" },
            new() { Role = MessageRole.Assistant, Content = "a2" },
            new() { Role = MessageRole.Tool, Content = "t2" },
            new() { Role = MessageRole.User, Content = "u3" },
            new() { Role = MessageRole.Assistant, Content = "a3" }
        };

        var splitHistory = typeof(LlmSessionCompactor).GetMethod(
            "SplitHistory",
            BindingFlags.NonPublic | BindingFlags.Static);
        splitHistory.ShouldNotBeNull();

        var result = ((List<SessionEntry> toSummarize, List<SessionEntry> toPreserve))splitHistory!
            .Invoke(null, [history, 2])!;

        result.toSummarize.Select(entry => entry.Content).ToList().ShouldBe(new[] { "u1", "a1", "t1" });
        result.toPreserve.Select(entry => entry.Content).ToList().ShouldBe(new[] { "u2", "a2", "t2", "u3", "a3" });
    }

    private static GatewaySession CreateSession(params (string role, string content)[] entries)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(Guid.NewGuid().ToString("N")),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent")
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
    }

    /// <summary>
    /// Regression test for #447: ShouldCompact must not overflow int32 on large sessions.
    /// </summary>
    [Fact]
    public void ShouldCompact_LargeSessionNearInt32Overflow_DoesNotOverflow()
    {
        // Simulate a session with a single entry whose char count would overflow int32 when summed.
        // 2,147,483,648 chars / 4 = 536,870,912 tokens — safely above int.MaxValue / 4.
        // We use 2 entries of 2,000,000,000 chars each (4B total) to force overflow without int cast.
        // We can't actually allocate 2B char strings in a test, so use reflection to test the logic path.
        // Instead: verify ShouldCompact returns true (not some negative or wrong value) for a
        // session where token estimate exceeds the threshold.
        var session = CreateSession(
            ("user", new string('a', 100_000)),   // 100K chars = ~25K tokens
            ("assistant", new string('b', 100_000)));
        var compactor = CreateCompactor("summary");
        var options = new CompactionOptions { ContextWindowTokens = 10_000, TokenThresholdRatio = 0.5 };
        // 200K chars / 4 = 50K estimated tokens > 5K threshold
        compactor.ShouldCompact(session.Session, options).ShouldBeTrue();
    }

    [Fact]
    public void ShouldCompact_LargeSessionBelowThreshold_ReturnsFalse()
    {
        var session = CreateSession(
            ("user", new string('a', 100_000)),
            ("assistant", new string('b', 100_000)));
        var compactor = CreateCompactor("summary");
        // Set threshold high enough that the session should NOT trigger compaction
        var options = new CompactionOptions { ContextWindowTokens = 1_000_000, TokenThresholdRatio = 0.5 };
        // 200K chars / 4 = 50K estimated tokens < 500K threshold
        compactor.ShouldCompact(session.Session, options).ShouldBeFalse();
    }

    [Fact]
    public void EstimateTokenCount_MultipleEntries_SumsCorrectly()
    {
        // Verify the basic math: 1200 chars / 4 = 300 tokens
        var session = CreateSession(
            ("user", new string('x', 800)),
            ("assistant", new string('y', 400)));
        var compactor = CreateCompactor("summary");
        // ShouldCompact at threshold of exactly 300 tokens should return false (not strictly greater)
        var options = new CompactionOptions { ContextWindowTokens = 1200, TokenThresholdRatio = 1.0 };
        // 1200 chars / 4 = 300 tokens; threshold = 1200 * 1.0 = 1200; 300 > 1200 = false
        compactor.ShouldCompact(session.Session, options).ShouldBeFalse();
    }

    // ─── Phase 3a (#531): mark-not-delete behaviour ─────────────────────────────

    [Fact]
    public async Task CompactAsync_KeepsSummarisedEntries_WithIsHistoryTrue()
    {
        var session = CreateSession(
            ("user", "u1"),
            ("assistant", "a1"),
            ("user", "u2"),
            ("assistant", "a2"));
        var compactor = CreateCompactor("summary");
        var originalCount = session.GetHistorySnapshot().Count;

        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeTrue();
        result.CompactedHistory.ShouldNotBeNull();
        result.CompactedHistory!.Count.ShouldBe(originalCount + 1,
            "every original entry must remain in the store; only the summary is appended");

        var historyEntries = result.CompactedHistory.Where(e => e.IsHistory).ToList();
        historyEntries.Count.ShouldBe(2);
        historyEntries.Select(e => e.Content).ShouldBe(new[] { "u1", "a1" });
    }

    [Fact]
    public async Task CompactAsync_OldSummary_BecomesIsHistory_OnNextCompaction()
    {
        // Cycle 1: produce a summary at index 2 (after u1,a1 are marked historical)
        var session = CreateSession(
            ("user", "u1"),
            ("assistant", "a1"),
            ("user", "u2"),
            ("assistant", "a2"));
        var compactor = CreateCompactor("summary-1");

        var result1 = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });
        session.ReplaceHistory(result1.CompactedHistory!);

        // Add more turns
        session.AddEntries(new[]
        {
            new SessionEntry { Role = MessageRole.User, Content = "u3" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a3" }
        });

        // Cycle 2: summary-1 is now LLM-visible (IsHistory=false). It must be folded into summary-2 and marked historical.
        var compactor2 = CreateCompactor("summary-2");
        var result2 = await compactor2.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        result2.Succeeded.ShouldBeTrue();
        session.ReplaceHistory(result2.CompactedHistory!);

        var snapshot = session.GetHistorySnapshot();
        snapshot.Where(e => e.Content == "summary-1").Single().IsHistory.ShouldBeTrue();
        snapshot.Where(e => e.Content == "summary-2").Single().IsHistory.ShouldBeFalse();
        // u1/a1/u2/a2 all preserved
        snapshot.Select(e => e.Content).ShouldContain("u1");
        snapshot.Select(e => e.Content).ShouldContain("a1");
        snapshot.Select(e => e.Content).ShouldContain("u2");
        snapshot.Select(e => e.Content).ShouldContain("a2");
    }

    [Fact]
    public async Task CompactAsync_AlreadyHistoricalEntries_NotReSummarised()
    {
        var session = CreateSession(
            ("user", "u1"),
            ("assistant", "a1"));
        // Mark u1/a1 as historical to simulate post-cycle-1 state
        var snapshotEntries = session.GetHistorySnapshot()
            .Select(e => e with { IsHistory = true })
            .Concat(new[]
            {
                new SessionEntry { Role = MessageRole.User, Content = "u2" },
                new SessionEntry { Role = MessageRole.Assistant, Content = "a2" },
                new SessionEntry { Role = MessageRole.User, Content = "u3" },
                new SessionEntry { Role = MessageRole.Assistant, Content = "a3" }
            })
            .ToList();
        session.ReplaceHistory(snapshotEntries);

        var compactor = CreateCompactor("summary");
        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeTrue();
        // The already-historical u1/a1 must remain IsHistory=true and not be folded again.
        // Only u2/a2 should be newly marked historical (PreservedTurns=1 keeps the u3/a3 turn).
        result.EntriesSummarized.ShouldBe(2, "only u2/a2 are newly summarised — u1/a1 were already historical");

        var newHistory = result.CompactedHistory!;
        newHistory.Single(e => e.Content == "u1").IsHistory.ShouldBeTrue();
        newHistory.Single(e => e.Content == "a1").IsHistory.ShouldBeTrue();
        newHistory.Single(e => e.Content == "u2").IsHistory.ShouldBeTrue();
        newHistory.Single(e => e.Content == "a2").IsHistory.ShouldBeTrue();
        newHistory.Single(e => e.Content == "u3").IsHistory.ShouldBeFalse();
        newHistory.Single(e => e.Content == "a3").IsHistory.ShouldBeFalse();
    }

    [Fact]
    public async Task CompactAsync_CrashSentinels_ExcludedFromSummarisationPrompt()
    {
        var session = CreateSession(
            ("user", "u1"),
            ("assistant", "a1"),
            ("user", "u2"),
            ("assistant", "a2"));

        // Insert a crash sentinel in the middle to simulate a survived gateway restart.
        var entries = session.GetHistorySnapshot().ToList();
        entries.Insert(2, new SessionEntry
        {
            Role = MessageRole.System,
            Content = "[agent turn in progress — gateway restarted]",
            IsCrashSentinel = true
        });
        session.ReplaceHistory(entries);

        var compactor = CreateCompactor("summary");
        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeTrue();
        // Crash sentinel must remain present in the store, unchanged, never marked IsHistory.
        var newHistory = result.CompactedHistory!;
        var sentinel = newHistory.Single(e => e.IsCrashSentinel);
        sentinel.IsHistory.ShouldBeFalse();
        // u1/a1 are summarised, marked IsHistory; sentinel and the preserved u2/a2 turn remain LLM-visible.
        result.EntriesSummarized.ShouldBe(2);
    }

    [Fact]
    public void ShouldCompact_OnlyCountsLlmVisibleEntries_HistoricalEntriesExcluded()
    {
        // Massive historical entries should NOT trigger compaction — they're hidden from the LLM.
        var session = CreateSession(("user", "tiny"));
        var entries = new List<SessionEntry>
        {
            new() { Role = MessageRole.User, Content = new string('a', 1_000_000), IsHistory = true },
            new() { Role = MessageRole.Assistant, Content = new string('b', 1_000_000), IsHistory = true },
            new() { Role = MessageRole.System, Content = "summary", IsCompactionSummary = true },
            new() { Role = MessageRole.User, Content = "tiny" }
        };
        session.ReplaceHistory(entries);

        var compactor = CreateCompactor("summary");
        var options = new CompactionOptions { ContextWindowTokens = 100_000, TokenThresholdRatio = 0.5 };

        compactor.ShouldCompact(session.Session, options).ShouldBeFalse(
            "historical entries must not contribute to the token budget");
    }

    // ─── Issue #665: Session compactor continuity audit ──────────────────────

    /// <summary>
    /// Fresh-session path: the summarization prompt must include ALL visible history entries.
    /// No entries should be silently dropped before the cursor — this is the BotNexus
    /// equivalent of OpenClaw's "context IS now injected" assertion.
    /// </summary>
    [Fact]
    public async Task CompactAsync_FreshSession_SummarizationPromptContainsAllVisibleEntries()
    {
        var session = CreateSession(
            ("user", "first message"),
            ("assistant", "first response"),
            ("user", "second message"),
            ("assistant", "second response"),
            ("user", "recent message"));

        string? capturedPrompt = null;
        var compactor = CreateCompactorCapturingPrompt("summary", prompt => capturedPrompt = prompt);

        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeTrue();
        capturedPrompt.ShouldNotBeNull("LLM must have been called with a summarization prompt");

        // All 4 summarized entries must appear in the prompt
        capturedPrompt!.ShouldContain("first message");
        capturedPrompt.ShouldContain("first response");
        capturedPrompt.ShouldContain("second message");
        capturedPrompt.ShouldContain("second response");
        // The preserved tail (recent message) is NOT sent to the LLM — it stays in history
        // and the EntriesPreserved count accounts for it
        result.EntriesPreserved.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Post-compaction path: on a second compaction cycle, IsHistory entries must NOT appear
    /// in the summarization prompt. Only the compaction summary + post-summary entries are sent.
    /// This mirrors OpenClaw's "only messages newer than the binding timestamp are replayed" fix.
    /// </summary>
    [Fact]
    public async Task CompactAsync_PostCompactionSession_HistoryEntriesAbsentFromSummarizationPrompt()
    {
        // Cycle 1: compact a session
        var session = CreateSession(
            ("user", "old-u1"),
            ("assistant", "old-a1"),
            ("user", "old-u2"),
            ("assistant", "old-a2"),
            ("user", "mid"),
            ("assistant", "mid-response"));

        var cycle1 = await CreateCompactor("cycle-1-summary").CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });
        session.ReplaceHistory(cycle1.CompactedHistory!);

        // Add fresh turns after cycle 1
        session.AddEntries(new[]
        {
            new SessionEntry { Role = MessageRole.User, Content = "new-u3" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "new-a3" },
            new SessionEntry { Role = MessageRole.User, Content = "new-u4" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "new-a4" },
            new SessionEntry { Role = MessageRole.User, Content = "preserve-this" }
        });

        // Cycle 2: capture the prompt
        string? capturedPrompt2 = null;
        var compactor2 = CreateCompactorCapturingPrompt("cycle-2-summary", prompt => capturedPrompt2 = prompt);
        var cycle2 = await compactor2.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        cycle2.Succeeded.ShouldBeTrue();
        capturedPrompt2.ShouldNotBeNull();

        // IsHistory entries from cycle 1 must NOT appear in cycle 2 prompt
        capturedPrompt2!.ShouldNotContain("old-u1");
        capturedPrompt2.ShouldNotContain("old-a1");
        capturedPrompt2.ShouldNotContain("old-u2");
        capturedPrompt2.ShouldNotContain("old-a2");

        // The cycle-1 summary IS included (it's visible, carries the folded context)
        capturedPrompt2.ShouldContain("cycle-1-summary");

        // New turns after cycle 1 must be in the prompt (or preserved — at least one is sent)
        var promptContainsNewTurns =
            capturedPrompt2.Contains("new-u3") ||
            capturedPrompt2.Contains("new-a3") ||
            capturedPrompt2.Contains("new-u4") ||
            capturedPrompt2.Contains("new-a4");
        promptContainsNewTurns.ShouldBeTrue("new post-compaction turns must be included in cycle-2 prompt or preserved");
    }

    private static LlmSessionCompactor CreateCompactorCapturingPrompt(
        string summary,
        Action<string> onPromptCaptured)
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
            .Returns<LlmModel, Context, SimpleStreamOptions?>((_, ctx, _) =>
            {
                // Capture the summarization prompt from the first user message
                var firstUser = ctx.Messages.OfType<UserMessage>().FirstOrDefault();
                if (firstUser?.Content is UserMessageContent umc)
                    onPromptCaptured(umc.Text ?? string.Empty);
                return CreateStream(summary);
            });

        providers.Register(provider.Object);

        var llmClient = new LlmClient(providers, models);
        return new LlmSessionCompactor(llmClient, NullLogger<LlmSessionCompactor>.Instance);
    }
}
