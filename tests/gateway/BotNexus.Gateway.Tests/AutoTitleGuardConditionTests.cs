using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for the auto-title guard condition logic.
/// Validates that compaction summaries, crash sentinels, historical entries, and tool
/// entries do not disrupt the "first user+assistant exchange" detection.
/// </summary>
public sealed class AutoTitleGuardConditionTests
{
    private static readonly AgentId TestAgentId = AgentId.From("test-agent");
    private static readonly ConversationId TestConversationId = ConversationId.From("c_test-conv");

    // ═══════════════════════════════════════════════════════════════════
    // IsLiveConversationEntry — individual entry filtering
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void IsLiveConversationEntry_CompactionSummary_ReturnsFalse()
    {
        var entry = new SessionEntry
        {
            Role = MessageRole.User,
            Content = "Compacted summary",
            IsCompactionSummary = true
        };

        ConversationAutoTitleService.IsLiveConversationEntry(entry).ShouldBeFalse();
    }

    [Fact]
    public void IsLiveConversationEntry_CrashSentinel_ReturnsFalse()
    {
        var entry = new SessionEntry
        {
            Role = MessageRole.User,
            Content = "Crash sentinel",
            IsCrashSentinel = true
        };

        ConversationAutoTitleService.IsLiveConversationEntry(entry).ShouldBeFalse();
    }

    [Fact]
    public void IsLiveConversationEntry_HistoricalEntry_ReturnsFalse()
    {
        var entry = new SessionEntry
        {
            Role = MessageRole.User,
            Content = "Old user message",
            IsHistory = true
        };

        ConversationAutoTitleService.IsLiveConversationEntry(entry).ShouldBeFalse();
    }

    [Fact]
    public void IsLiveConversationEntry_ToolEntry_ReturnsFalse()
    {
        var entry = new SessionEntry
        {
            Role = MessageRole.Tool,
            Content = "tool result",
            ToolName = "web_search"
        };

        ConversationAutoTitleService.IsLiveConversationEntry(entry).ShouldBeFalse();
    }

    [Fact]
    public void IsLiveConversationEntry_NormalUserEntry_ReturnsTrue()
    {
        var entry = new SessionEntry
        {
            Role = MessageRole.User,
            Content = "Normal message"
        };

        ConversationAutoTitleService.IsLiveConversationEntry(entry).ShouldBeTrue();
    }

    [Fact]
    public void IsLiveConversationEntry_NormalAssistantEntry_ReturnsTrue()
    {
        var entry = new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = "Normal response"
        };

        ConversationAutoTitleService.IsLiveConversationEntry(entry).ShouldBeTrue();
    }

    [Fact]
    public void IsLiveConversationEntry_SystemEntry_ReturnsFalse()
    {
        var entry = new SessionEntry
        {
            Role = MessageRole.System,
            Content = "System prompt"
        };

        ConversationAutoTitleService.IsLiveConversationEntry(entry).ShouldBeFalse();
    }

    [Fact]
    public void IsLiveConversationEntry_NotificationEntry_ReturnsFalse()
    {
        var entry = new SessionEntry
        {
            Role = MessageRole.Notification,
            Content = "Gateway restarted"
        };

        ConversationAutoTitleService.IsLiveConversationEntry(entry).ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════
    // ShouldTriggerAutoTitle — combined guard logic
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ShouldTrigger_CompactionPlusOneUser_ReturnsTexts()
    {
        var history = new List<SessionEntry>
        {
            new() { Role = MessageRole.Assistant, Content = "Summary of prior conversation", IsCompactionSummary = true },
            new() { Role = MessageRole.User, Content = "What is the weather?" },
            new() { Role = MessageRole.Assistant, Content = "It is sunny today." }
        };

        var (userText, assistantText) = ConversationAutoTitleService.ShouldTriggerAutoTitle(history);

        userText.ShouldBe("What is the weather?");
        assistantText.ShouldBe("It is sunny today.");
    }

    [Fact]
    public void ShouldTrigger_MultipleRealUsers_ReturnsNull()
    {
        var history = new List<SessionEntry>
        {
            new() { Role = MessageRole.User, Content = "First question" },
            new() { Role = MessageRole.Assistant, Content = "First answer" },
            new() { Role = MessageRole.User, Content = "Second question" },
            new() { Role = MessageRole.Assistant, Content = "Second answer" }
        };

        var (userText, assistantText) = ConversationAutoTitleService.ShouldTriggerAutoTitle(history);

        userText.ShouldBeNull();
        assistantText.ShouldBeNull();
    }

    [Fact]
    public void ShouldTrigger_ToolHeavyFirstTurn_ReturnsTexts()
    {
        var history = new List<SessionEntry>
        {
            new() { Role = MessageRole.User, Content = "Find me a hotel" },
            new() { Role = MessageRole.Assistant, Content = "" },
            new() { Role = MessageRole.Tool, Content = "hotel results", ToolName = "web_search" },
            new() { Role = MessageRole.Assistant, Content = "I found several hotels for you." }
        };

        var (userText, assistantText) = ConversationAutoTitleService.ShouldTriggerAutoTitle(history);

        userText.ShouldBe("Find me a hotel");
        assistantText.ShouldBe("I found several hotels for you.");
    }

    [Fact]
    public void ShouldTrigger_NoAssistantEntry_ReturnsNull()
    {
        var history = new List<SessionEntry>
        {
            new() { Role = MessageRole.User, Content = "Hello" }
        };

        var (userText, assistantText) = ConversationAutoTitleService.ShouldTriggerAutoTitle(history);

        userText.ShouldBeNull();
        assistantText.ShouldBeNull();
    }

    [Fact]
    public void ShouldTrigger_OnlyCrashSentinels_ReturnsNull()
    {
        var history = new List<SessionEntry>
        {
            new() { Role = MessageRole.User, Content = "sentinel", IsCrashSentinel = true },
            new() { Role = MessageRole.Assistant, Content = "response", IsCrashSentinel = true }
        };

        var (userText, assistantText) = ConversationAutoTitleService.ShouldTriggerAutoTitle(history);

        userText.ShouldBeNull();
        assistantText.ShouldBeNull();
    }

    [Fact]
    public void ShouldTrigger_EmptyAssistantText_UsesLastNonEmpty()
    {
        var history = new List<SessionEntry>
        {
            new() { Role = MessageRole.User, Content = "Search for cats" },
            new() { Role = MessageRole.Assistant, Content = "" },
            new() { Role = MessageRole.Tool, Content = "results", ToolName = "web_search" },
            new() { Role = MessageRole.Assistant, Content = "Here are the results about cats." }
        };

        var (userText, assistantText) = ConversationAutoTitleService.ShouldTriggerAutoTitle(history);

        userText.ShouldBe("Search for cats");
        assistantText.ShouldBe("Here are the results about cats.");
    }

    [Fact]
    public void ShouldTrigger_HistoricalUserEntries_Excluded()
    {
        var history = new List<SessionEntry>
        {
            new() { Role = MessageRole.User, Content = "Old question 1", IsHistory = true },
            new() { Role = MessageRole.Assistant, Content = "Old answer 1", IsHistory = true },
            new() { Role = MessageRole.User, Content = "Old question 2", IsHistory = true },
            new() { Role = MessageRole.Assistant, Content = "Old answer 2", IsHistory = true },
            new() { Role = MessageRole.Assistant, Content = "Compaction summary", IsCompactionSummary = true },
            new() { Role = MessageRole.User, Content = "Fresh question" },
            new() { Role = MessageRole.Assistant, Content = "Fresh answer" }
        };

        var (userText, assistantText) = ConversationAutoTitleService.ShouldTriggerAutoTitle(history);

        userText.ShouldBe("Fresh question");
        assistantText.ShouldBe("Fresh answer");
    }

    [Fact]
    public void ShouldTrigger_EmptyHistory_ReturnsNull()
    {
        var history = new List<SessionEntry>();

        var (userText, assistantText) = ConversationAutoTitleService.ShouldTriggerAutoTitle(history);

        userText.ShouldBeNull();
        assistantText.ShouldBeNull();
    }
}
