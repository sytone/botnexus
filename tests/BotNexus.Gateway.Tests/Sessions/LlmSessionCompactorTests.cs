using System.Reflection;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.Core.Streaming;
using FluentAssertions;
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

        compactor.ShouldCompact(session.Session, options).Should().BeFalse();
    }

    [Fact]
    public void ShouldCompact_AboveThreshold_ReturnsTrue()
    {
        var session = CreateSession(("user", new string('a', 300)));
        var compactor = CreateCompactor("summary");
        var options = new CompactionOptions { ContextWindowTokens = 100, TokenThresholdRatio = 0.5 };

        compactor.ShouldCompact(session.Session, options).Should().BeTrue();
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

        await compactor.CompactAsync(session.Session, new CompactionOptions
        {
            PreservedTurns = 2,
            SummarizationModel = TestModel.Id
        });

        session.GetHistorySnapshot().Select(entry => entry.Content).Should().ContainInOrder(
            "summary-u1",
            "u2",
            "a2",
            "t2",
            "u3",
            "a3");
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

        result.EntriesSummarized.Should().Be(2);
        result.EntriesPreserved.Should().Be(2);
        session.GetHistorySnapshot().Select(entry => entry.Content)
            .Should().ContainInOrder("structured summary", "recent", "recent-response");
    }

    [Fact]
    public async Task CompactAsync_EmptyHistory_ReturnsEmptyResult()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("s1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("a1") };
        var compactor = CreateCompactor("summary");

        var result = await compactor.CompactAsync(session.Session, new CompactionOptions());

        result.Summary.Should().BeEmpty();
        result.EntriesSummarized.Should().Be(0);
        result.EntriesPreserved.Should().Be(0);
        session.GetHistorySnapshot().Should().BeEmpty();
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

        result.Summary.Should().BeEmpty();
        result.EntriesSummarized.Should().Be(0);
        result.EntriesPreserved.Should().Be(4);
        session.GetHistorySnapshot().Should().BeEquivalentTo(originalHistory, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task CompactAsync_SetsIsCompactionSummaryFlag()
    {
        var session = CreateSession(
            ("user", "older"),
            ("assistant", "older-response"),
            ("user", "recent"));
        var compactor = CreateCompactor("compacted");

        await compactor.CompactAsync(session.Session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        var summaryEntry = session.GetHistorySnapshot().First();
        summaryEntry.Role.Should().Be(MessageRole.System);
        summaryEntry.IsCompactionSummary.Should().BeTrue();
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

        result.Summary.Length.Should().Be(40);
        session.GetHistorySnapshot().First().Content.Length.Should().Be(40);
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
        splitHistory.Should().NotBeNull();

        var result = ((List<SessionEntry> toSummarize, List<SessionEntry> toPreserve))splitHistory!
            .Invoke(null, [history, 2])!;

        result.toSummarize.Select(entry => entry.Content).Should().ContainInOrder("u1", "a1", "t1");
        result.toPreserve.Select(entry => entry.Content).Should().ContainInOrder("u2", "a2", "t2", "u3", "a3");
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
}

