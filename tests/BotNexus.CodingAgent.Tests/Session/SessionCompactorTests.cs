using BotNexus.CodingAgent.Session;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.Core.Streaming;
using FluentAssertions;
using Moq;
using AgentMessage = BotNexus.AgentCore.Types.AgentMessage;
using AgentUserMessage = BotNexus.AgentCore.Types.UserMessage;
using AssistantAgentMessage = BotNexus.AgentCore.Types.AssistantAgentMessage;
using ToolResultAgentMessage = BotNexus.AgentCore.Types.ToolResultAgentMessage;
using AgentToolResult = BotNexus.AgentCore.Types.AgentToolResult;
using AgentToolContent = BotNexus.AgentCore.Types.AgentToolContent;
using AgentToolContentType = BotNexus.AgentCore.Types.AgentToolContentType;

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

        compacted.Should().BeEquivalentTo(messages, options => options.WithStrictOrdering());
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

        compacted.Should().HaveCount(3);
        compacted[0].Should().BeOfType<AgentUserMessage>();
        compacted[0].As<AgentUserMessage>().Content.Should().Contain("3 earlier messages compacted");
        compacted[0].As<AgentUserMessage>().Content.Should().Contain("GrepTool.cs");
        compacted[1].Should().Be(messages[3]);
        compacted[2].Should().Be(messages[4]);
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
        var summary = compacted[0].As<AgentUserMessage>().Content;

        summary.Should().Contain("Key topics discussed:");
        summary.Should().Contain("Files modified:");
        summary.Should().Contain("Decisions made:");
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

        compacted[0].Should().BeOfType<AgentUserMessage>();
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

        compacted[^1].Should().BeSameAs(lastMessage);
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

        compacted[0].As<AgentUserMessage>().Content.Should().Contain("<modified-files>");
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

        compacted[0].As<AgentUserMessage>().Content.Should().Contain("## Goal");
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
        var summary = compacted[0].As<AgentUserMessage>().Content;

        summary.Should().NotContain("Turn Context (split turn)");
        compacted.Skip(1).OfType<AgentUserMessage>().Select(message => message.Content)
            .Should().ContainSingle().Which.Should().Be("keep this recent");
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
            .Should().OnlyContain(toolResult => remainingToolCallIds.Contains(toolResult.ToolCallId));
    }

    [Fact]
    public void EstimateTokens_AssistantUsagePresent_UsesUsageTokenCounts()
    {
        var compactor = new SessionCompactor();
        var messages = new AgentMessage[]
        {
            new AssistantAgentMessage(
                Content: new string('x', 4000),
                Usage: new BotNexus.AgentCore.Types.AgentUsage(InputTokens: 7, OutputTokens: 11))
        };

        compactor.EstimateTokens(messages).Should().Be(18);
    }
}
