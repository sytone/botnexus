using BotNexus.CodingAgent.Session;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using Moq;
using AgentMessage = BotNexus.Agent.Core.Types.AgentMessage;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;
using AssistantAgentMessage = BotNexus.Agent.Core.Types.AssistantAgentMessage;
using SystemAgentMessage = BotNexus.Agent.Core.Types.SystemAgentMessage;
using ToolResultAgentMessage = BotNexus.Agent.Core.Types.ToolResultAgentMessage;
using AgentToolResult = BotNexus.Agent.Core.Types.AgentToolResult;
using AgentToolContent = BotNexus.Agent.Core.Types.AgentToolContent;
using AgentToolContentType = BotNexus.Agent.Core.Types.AgentToolContentType;

namespace BotNexus.CodingAgent.Tests.Session;

public sealed class SessionCompactorTests
{
    private static LlmModel MakeModel() => new(
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

    private static LlmClient MakeClientReturningSummary(string summary)
    {
        var registry = new ApiProviderRegistry();
        var models = new ModelRegistry();
        var provider = new Mock<IApiProvider>();
        provider.SetupGet(item => item.Api).Returns("test-api");

        var stream = new LlmStream();
        var completion = new AssistantMessage(
            Content: [new TextContent(summary)],
            Api: "test-api",
            Provider: "test-provider",
            ModelId: "test-model",
            Usage: Usage.Empty(),
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        stream.Push(new DoneEvent(StopReason.Stop, completion));
        stream.End(completion);

        provider.Setup(item => item.StreamSimple(
                It.IsAny<LlmModel>(),
                It.IsAny<Context>(),
                It.IsAny<SimpleStreamOptions>()))
            .Returns(stream);
        registry.Register(provider.Object);

        return new LlmClient(registry, models);
    }

    [Fact]
    public void Compact_WhenMessageCountWithinKeepRecent_ReturnsOriginalMessages()
    {
        var messages = new AgentMessage[]
        {
            new AgentUserMessage("hello"),
            new AssistantAgentMessage("hi")
        };

        var compactor = new SessionCompactor();
        var compacted = compactor.Compact(messages, keepRecentCount: 5);

        compacted.ShouldBe(messages);
    }

    [Fact]
    public void Compact_WhenMessagesExceedKeepRecent_ReplacesOldMessagesWithSummary()
    {
        var messages = new AgentMessage[]
        {
            new AgentUserMessage("We should add grep support for source search."),
            new AssistantAgentMessage("We will implement a new tool."),
            new ToolResultAgentMessage("1", "edit", new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "Updated src/coding-agent/BotNexus.CodingAgent/Tools/GrepTool.cs")])),
            new AgentUserMessage("Keep this recent."),
            new AssistantAgentMessage("Recent response.")
        };

        var compactor = new SessionCompactor();
        var compacted = compactor.Compact(messages, keepRecentCount: 2);

        compacted.Count().ShouldBe(3);
        var first = compacted[0].ShouldBeOfType<SystemAgentMessage>();
        first.Content.ShouldContain("3 earlier messages compacted");
        first.Content.ShouldContain("GrepTool.cs");
        compacted[1].ShouldBe(messages[3]);
        compacted[2].ShouldBe(messages[4]);
    }

    [Fact]
    public void Compact_WhenNoStructuredInsights_FallsBackToDefaultSummaryValues()
    {
        var messages = new AgentMessage[]
        {
            new AgentUserMessage("ok"),
            new AssistantAgentMessage("done"),
            new AgentUserMessage("recent")
        };

        var compactor = new SessionCompactor();
        var compacted = compactor.Compact(messages, keepRecentCount: 1);
        var summary = compacted[0].ShouldBeOfType<SystemAgentMessage>().Content;

        summary.ShouldContain("Key topics discussed:");
        summary.ShouldContain("Files modified:");
        summary.ShouldContain("Decisions made:");
    }

    [Fact]
    public async Task CompactAsync_WhenTokenEstimateExceedsThreshold_CompactsConversation()
    {
        var compactor = new SessionCompactor();
        var messages = new AgentMessage[]
        {
            new AgentUserMessage(new string('a', 300)),
            new AssistantAgentMessage("working on it"),
            new AgentUserMessage("keep this message")
        };
        var options = new SessionCompactor.SessionCompactionOptions(
            MaxContextTokens: 20,
            ReserveTokens: 1,
            KeepRecentTokens: 5,
            KeepRecentCount: 1,
            LlmClient: MakeClientReturningSummary("## Goal\nTest summary"),
            Model: MakeModel());

        var compacted = await compactor.CompactAsync(messages, options);

        compacted[0].ShouldBeOfType<SystemAgentMessage>();
    }

    [Fact]
    public async Task CompactAsync_WhenCompacting_KeepsRecentMessagesIntact()
    {
        var compactor = new SessionCompactor();
        var lastMessage = new AssistantAgentMessage("most recent");
        var messages = new AgentMessage[]
        {
            new AgentUserMessage(new string('a', 300)),
            new AssistantAgentMessage("older response"),
            lastMessage
        };
        var options = new SessionCompactor.SessionCompactionOptions(
            MaxContextTokens: 20,
            ReserveTokens: 1,
            KeepRecentTokens: 5,
            KeepRecentCount: 1,
            LlmClient: MakeClientReturningSummary("## Goal\nRecent message preserved"),
            Model: MakeModel());

        var compacted = await compactor.CompactAsync(messages, options);

        compacted[^1].ShouldBeSameAs(lastMessage);
    }

    [Fact]
    public async Task CompactAsync_WhenFileOperationsExist_PreservesFileOperationTracking()
    {
        var compactor = new SessionCompactor();
        var messages = new AgentMessage[]
        {
            new AssistantAgentMessage(
                "running tools",
                [
                    new ToolCallContent("1", "read", new Dictionary<string, object?> { ["path"] = "src/alpha.cs" }),
                    new ToolCallContent("2", "edit", new Dictionary<string, object?> { ["path"] = "src/beta.cs" })
                ]),
            new ToolResultAgentMessage("1", "read", new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "Read src/alpha.cs")]))
            ,
            new AgentUserMessage(new string('a', 300)),
            new AssistantAgentMessage("recent")
        };

        var options = new SessionCompactor.SessionCompactionOptions(
            MaxContextTokens: 20,
            ReserveTokens: 1,
            KeepRecentTokens: 5,
            KeepRecentCount: 1,
            LlmClient: MakeClientReturningSummary("## Goal\nTrack files"),
            Model: MakeModel());

        var compacted = await compactor.CompactAsync(messages, options);

        compacted[0].ShouldBeOfType<SystemAgentMessage>().Content.ShouldContain("<modified-files>");
    }

    [Fact]
    public async Task CompactAsync_WhenLlmSummaryGenerated_UsesSummaryAsUserMessage()
    {
        var compactor = new SessionCompactor();
        var messages = new AgentMessage[]
        {
            new AgentUserMessage(new string('a', 300)),
            new AssistantAgentMessage("older response"),
            new AssistantAgentMessage("recent")
        };
        var options = new SessionCompactor.SessionCompactionOptions(
            MaxContextTokens: 20,
            ReserveTokens: 1,
            KeepRecentTokens: 5,
            KeepRecentCount: 1,
            LlmClient: MakeClientReturningSummary("## Goal\nLLM summary"),
            Model: MakeModel());

        var compacted = await compactor.CompactAsync(messages, options);

        compacted[0].ShouldBeOfType<SystemAgentMessage>().Content.ShouldContain("## Goal");
    }

    [Fact]
    public async Task CompactAsync_WhenCutFallsMidTurn_AdvancesToUserTurnBoundary()
    {
        var compactor = new SessionCompactor();
        var messages = new AgentMessage[]
        {
            new AgentUserMessage(new string('u', 300)),
            new AssistantAgentMessage("Starting task",
            [
                new ToolCallContent("1", "read", new Dictionary<string, object?> { ["path"] = "src/file.cs" })
            ]),
            new ToolResultAgentMessage("1", "read", new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "file contents")])),
            new AssistantAgentMessage("Continuing with edits"),
            new AgentUserMessage("keep this recent")
        };

        var options = new SessionCompactor.SessionCompactionOptions(
            MaxContextTokens: 20,
            ReserveTokens: 1,
            KeepRecentTokens: 5,
            KeepRecentCount: 1,
            LlmClient: MakeClientReturningSummary("## Goal\nCompacted"),
            Model: MakeModel());

        var compacted = await compactor.CompactAsync(messages, options);
        var summary = compacted[0].ShouldBeOfType<SystemAgentMessage>().Content;

        summary.ShouldNotContain("Turn Context (split turn)");
        compacted.Skip(1).OfType<AgentUserMessage>().Select(message => message.Content)
            .ShouldHaveSingleItem().ShouldBe("keep this recent");
    }

    [Fact]
    public async Task CompactAsync_NeverSplitsAssistantToolCallAndToolResultPair()
    {
        var compactor = new SessionCompactor();
        var messages = new AgentMessage[]
        {
            new AgentUserMessage("Please read the file"),
            new AssistantAgentMessage(
                "Calling read tool",
                [
                    new ToolCallContent("call-1", "read", new Dictionary<string, object?> { ["path"] = "src/file.cs" })
                ],
                StopReason.ToolUse),
            new ToolResultAgentMessage(
                "call-1",
                "read",
                new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "file content payload")])),
            new AgentUserMessage("Continue"),
            new AssistantAgentMessage(new string('x', 20))
        };

        var options = new SessionCompactor.SessionCompactionOptions(
            MaxContextTokens: 6,
            ReserveTokens: 1,
            KeepRecentTokens: 5,
            KeepRecentCount: 1,
            LlmClient: MakeClientReturningSummary("## Goal\nCompacted"),
            Model: MakeModel());

        var compacted = await compactor.CompactAsync(messages, options);
        var remainingToolCallIds = compacted
            .OfType<AssistantAgentMessage>()
            .SelectMany(message => message.ToolCalls ?? [])
            .Select(toolCall => toolCall.Id)
            .ToHashSet(StringComparer.Ordinal);

        compacted.OfType<ToolResultAgentMessage>()
            .ShouldAllBe(toolResult => remainingToolCallIds.Contains(toolResult.ToolCallId));
    }

    [Fact]
    public void EstimateTokens_AssistantUsagePresent_UsesUsageTokenCounts()
    {
        var compactor = new SessionCompactor();
        var messages = new AgentMessage[]
        {
            new AssistantAgentMessage(
                Content: new string('x', 4000),
                Usage: new BotNexus.Agent.Core.Types.AgentUsage(InputTokens: 7, OutputTokens: 11))
        };

        compactor.EstimateTokens(messages).ShouldBe(18);
    }
}
