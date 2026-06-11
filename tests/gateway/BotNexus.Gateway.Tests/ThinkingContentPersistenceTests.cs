using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Tests;

public sealed class ThinkingContentPersistenceTests
{
    [Fact]
    public void SessionEntry_ThinkingContent_defaults_to_null()
    {
        var entry = new SessionEntry { Role = MessageRole.Assistant, Content = "Hello" };
        Assert.Null(entry.ThinkingContent);
    }

    [Fact]
    public void SessionEntry_ThinkingContent_round_trips()
    {
        var entry = new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = "Response",
            ThinkingContent = "I should think about this carefully."
        };
        Assert.Equal("I should think about this carefully.", entry.ThinkingContent);
    }

    [Fact]
    public void SessionEntry_with_record_copies_ThinkingContent()
    {
        var original = new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = "Hello",
            ThinkingContent = "Reasoning here"
        };
        var copy = original with { Content = "Updated" };
        Assert.Equal("Reasoning here", copy.ThinkingContent);
    }
}
