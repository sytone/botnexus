using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent.Session;
using FluentAssertions;

namespace BotNexus.CodingAgent.Tests.Session;

public sealed class SessionCompactorTests
{
    [Fact]
    public void Compact_WhenMessageCountWithinKeepRecent_ReturnsOriginalMessages()
    {
        var messages = new AgentMessage[]
        {
            new UserMessage("hello"),
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
            new UserMessage("We should add grep support for source search."),
            new AssistantAgentMessage("We will implement a new tool."),
            new ToolResultAgentMessage("1", "edit", new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "Updated src/coding-agent/BotNexus.CodingAgent/Tools/GrepTool.cs")])),
            new UserMessage("Keep this recent."),
            new AssistantAgentMessage("Recent response.")
        };

        var compactor = new SessionCompactor();
        var compacted = compactor.Compact(messages, keepRecentCount: 2);

        compacted.Should().HaveCount(3);
        compacted[0].Should().BeOfType<SystemAgentMessage>();
        compacted[0].As<SystemAgentMessage>().Content.Should().Contain("3 earlier messages compacted");
        compacted[0].As<SystemAgentMessage>().Content.Should().Contain("GrepTool.cs");
        compacted[1].Should().Be(messages[3]);
        compacted[2].Should().Be(messages[4]);
    }

    [Fact]
    public void Compact_WhenNoStructuredInsights_FallsBackToDefaultSummaryValues()
    {
        var messages = new AgentMessage[]
        {
            new UserMessage("ok"),
            new AssistantAgentMessage("done"),
            new UserMessage("recent")
        };

        var compactor = new SessionCompactor();
        var compacted = compactor.Compact(messages, keepRecentCount: 1);
        var summary = compacted[0].As<SystemAgentMessage>().Content;

        summary.Should().Contain("Key topics discussed:");
        summary.Should().Contain("Files modified:");
        summary.Should().Contain("Decisions made:");
    }
}
