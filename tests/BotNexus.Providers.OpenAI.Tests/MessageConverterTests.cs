using System.Text.Json;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.Providers.OpenAI.Tests;

public class MessageConverterTests
{
    private static readonly long Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public void UserMessageContent_WithText_IsTextTrue()
    {
        var content = new UserMessageContent("hello");

        content.IsText.Should().BeTrue();
        content.Text.Should().Be("hello");
        content.Blocks.Should().BeNull();
    }

    [Fact]
    public void UserMessageContent_WithBlocks_IsTextFalse()
    {
        var blocks = new ContentBlock[]
        {
            new TextContent("hi"),
            new ImageContent("data", "image/png")
        };
        var content = new UserMessageContent(blocks);

        content.IsText.Should().BeFalse();
        content.Blocks.Should().HaveCount(2);
    }

    [Fact]
    public void UserMessageContent_ImplicitConversionFromString()
    {
        UserMessageContent content = "hello";

        content.IsText.Should().BeTrue();
        content.Text.Should().Be("hello");
    }

    [Fact]
    public void UserMessageContent_JsonSerialization_StringContent()
    {
        var content = new UserMessageContent("test message");
        var json = JsonSerializer.Serialize(content);

        json.Should().Be("\"test message\"");
    }

    [Fact]
    public void UserMessageContent_JsonDeserialization_FromString()
    {
        var json = "\"hello world\"";
        var content = JsonSerializer.Deserialize<UserMessageContent>(json);

        content.Should().NotBeNull();
        content!.IsText.Should().BeTrue();
        content.Text.Should().Be("hello world");
    }

    [Fact]
    public void AssistantMessage_WithToolCalls_HasCorrectStructure()
    {
        var args = new Dictionary<string, object?> { ["path"] = "/tmp" };
        var content = new ContentBlock[]
        {
            new TextContent("Let me read that file"),
            new ToolCallContent("call_123", "read_file", args)
        };

        var msg = new AssistantMessage(
            Content: content,
            Api: "openai-completions",
            Provider: "openai",
            ModelId: "gpt-4o",
            Usage: Usage.Empty(),
            StopReason: StopReason.ToolUse,
            ErrorMessage: null,
            ResponseId: "resp-1",
            Timestamp: Ts);

        msg.Content.Should().HaveCount(2);
        msg.Content[0].Should().BeOfType<TextContent>();
        msg.Content[1].Should().BeOfType<ToolCallContent>();
        var tc = (ToolCallContent)msg.Content[1];
        tc.Id.Should().Be("call_123");
        tc.Name.Should().Be("read_file");
    }

    [Fact]
    public void ToolResultMessage_ConvertsToCorrectStructure()
    {
        var msg = new ToolResultMessage(
            ToolCallId: "call_123",
            ToolName: "read_file",
            Content: [new TextContent("file contents here")],
            IsError: false,
            Timestamp: Ts);

        msg.ToolCallId.Should().Be("call_123");
        msg.ToolName.Should().Be("read_file");
        msg.IsError.Should().BeFalse();
    }

    [Fact]
    public void OpenAICompletionsCompat_DeveloperRole_DefaultTrue()
    {
        var compat = new OpenAICompletionsCompat();

        compat.SupportsDeveloperRole.Should().BeTrue();
    }

    [Fact]
    public void OpenAICompletionsCompat_SystemRoleFallback_WhenDeveloperRoleFalse()
    {
        var compat = new OpenAICompletionsCompat { SupportsDeveloperRole = false };

        compat.SupportsDeveloperRole.Should().BeFalse();
    }

    [Fact]
    public void ThinkingContent_WhenRequiresThinkingAsText_CompatFlagSet()
    {
        var compat = new OpenAICompletionsCompat { RequiresThinkingAsText = true };

        compat.RequiresThinkingAsText.Should().BeTrue();
    }
}
