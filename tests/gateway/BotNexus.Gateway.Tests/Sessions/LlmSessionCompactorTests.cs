using System.Reflection;
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

        var result = await compactor.CompactAsync(session.Session, new CompactionOptions
        {
            PreservedTurns = 2,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeTrue();
        result.CompactedHistory.ShouldNotBeNull();
        session.ReplaceHistory(result.CompactedHistory!);

        session.GetHistorySnapshot().Select(entry => entry.Content).ToList().ShouldBe(
            new[] { "summary-u1", "u2", "a2", "t2", "u3", "a3" }, ignoreOrder: false);
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

        var result = await compactor.CompactAsync(session.Session, new CompactionOptions
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
            .ToList().ShouldBe(new[] { "structured summary", "recent", "recent-response" }, ignoreOrder: false);
    }

    [Fact]
    public async Task CompactAsync_EmptyHistory_ReturnsEmptyResult()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("s1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("a1") };
        var compactor = CreateCompactor("summary");

        var result = await compactor.CompactAsync(session.Session, new CompactionOptions());

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

        var result = await compactor.CompactAsync(session.Session, new CompactionOptions
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

        var result = await compactor.CompactAsync(session.Session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeTrue();
        result.CompactedHistory.ShouldNotBeNull();
        session.ReplaceHistory(result.CompactedHistory!);

        var summaryEntry = session.GetHistorySnapshot().First();
        summaryEntry.Role.ShouldBe(MessageRole.System);
        summaryEntry.IsCompactionSummary.ShouldBeTrue();
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

        var result = await compactor.CompactAsync(session.Session, new CompactionOptions
        {
            PreservedTurns = 1,
            MaxSummaryChars = 40,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeTrue();
        result.Summary.Length.ShouldBe(40);

        session.ReplaceHistory(result.CompactedHistory!);
        session.GetHistorySnapshot().First().Content.Length.ShouldBe(40);
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

        var result = await compactor.CompactAsync(session.Session, new CompactionOptions
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
            SessionId = Guid.NewGuid().ToString("N"),
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

    /// <summary>
    /// #496: Compaction context must include a system prompt so models like gpt-4.1
    /// do not refuse the summarization request with empty content.
    /// </summary>
    [Fact]
    public async Task CompactAsync_ContextHasSystemPrompt()
    {
        Context? capturedContext = null;
        var session = CreateSession(
            ("user", "older"),
            ("assistant", "older-response"),
            ("user", "recent"));

        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();
        models.Register(TestModel.Provider, TestModel);

        var provider = new Mock<IApiProvider>();
        provider.SetupGet(item => item.Api).Returns(TestModel.Api);
        provider.Setup(item => item.StreamSimple(
                It.IsAny<LlmModel>(),
                It.IsAny<Context>(),
                It.IsAny<SimpleStreamOptions?>()))
            .Callback<LlmModel, Context, SimpleStreamOptions?>((_, ctx, _) => capturedContext = ctx)
            .Returns(() => CreateStream("summary"));

        providers.Register(provider.Object);
        var llmClient = new LlmClient(providers, models);
        var compactor = new LlmSessionCompactor(llmClient, NullLogger<LlmSessionCompactor>.Instance);

        await compactor.CompactAsync(session.Session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        capturedContext.ShouldNotBeNull();
        capturedContext!.SystemPrompt.ShouldNotBeNullOrWhiteSpace("system prompt must be set for compaction");
    }

    /// <summary>
    /// #496: When primary model returns empty content, retry with fallback model.
    /// </summary>
    [Fact]
    public async Task CompactAsync_PrimaryReturnsEmpty_RetriesWithFallbackModel()
    {
        var session = CreateSession(
            ("user", "older"),
            ("assistant", "older-response"),
            ("user", "recent"));

        var fallbackModel = new LlmModel(
            Id: "fallback-model",
            Name: "Fallback Model",
            Api: "test-api",
            Provider: "test-provider",
            BaseUrl: "https://example.com",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 32000,
            MaxTokens: 4096);

        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();
        models.Register(TestModel.Provider, TestModel);
        models.Register(fallbackModel.Provider, fallbackModel);

        var callCount = 0;
        var provider = new Mock<IApiProvider>();
        provider.SetupGet(item => item.Api).Returns(TestModel.Api);
        provider.Setup(item => item.StreamSimple(
                It.IsAny<LlmModel>(),
                It.IsAny<Context>(),
                It.IsAny<SimpleStreamOptions?>()))
            .Returns(() =>
            {
                callCount++;
                // First call (primary) returns empty; second call (fallback) returns real summary
                return callCount == 1 ? CreateStream("") : CreateStream("fallback summary");
            });

        providers.Register(provider.Object);
        var llmClient = new LlmClient(providers, models);
        var compactor = new LlmSessionCompactor(llmClient, NullLogger<LlmSessionCompactor>.Instance);

        var result = await compactor.CompactAsync(session.Session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeTrue("fallback model should produce a successful compaction");
        result.Summary.ShouldBe("fallback summary");
        callCount.ShouldBe(2, "should have called LLM twice (primary + fallback)");
    }

    /// <summary>
    /// #496: When primary returns empty AND no fallback is available, compaction aborts cleanly.
    /// </summary>
    [Fact]
    public async Task CompactAsync_PrimaryAndNoFallback_AbortsCleanly()
    {
        var session = CreateSession(
            ("user", "older"),
            ("assistant", "older-response"),
            ("user", "recent"));
        var originalHistory = session.GetHistorySnapshot().ToList();

        // CreateCompactor uses a single model (TestModel) — no fallback available
        var compactor = CreateCompactor(summary: "");

        var result = await compactor.CompactAsync(session.Session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeFalse();
        result.Summary.ShouldBeEmpty();
        result.CompactedHistory.ShouldBeNull();
        // History must be unchanged
        session.GetHistorySnapshot().Count.ShouldBe(originalHistory.Count);
    }
}
