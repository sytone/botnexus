using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

public class AnthropicMessageConverterTests
{
    private static readonly long Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public void UserTextMessage_HasCorrectStructure()
    {
        var msg = new UserMessage(new UserMessageContent("hello"), Ts);

        msg.Content.IsText.ShouldBeTrue();
        msg.Content.Text.ShouldBe("hello");
    }

    [Fact]
    public void UserMultimodalMessage_WithImage_HasBlocks()
    {
        var blocks = new ContentBlock[]
        {
            new TextContent("describe this"),
            new ImageContent("aGVsbG8=", "image/png")
        };
        var msg = new UserMessage(new UserMessageContent(blocks), Ts);

        msg.Content.IsText.ShouldBeFalse();
        msg.Content.Blocks!.Count().ShouldBe(2);
        msg.Content.Blocks![1].ShouldBeOfType<ImageContent>();
        var image = (ImageContent)msg.Content.Blocks[1];
        image.MimeType.ShouldBe("image/png");
        image.Data.ShouldBe("aGVsbG8=");
    }

    [Fact]
    public void AssistantTextBlock_HasCorrectType()
    {
        var block = new TextContent("response text");

        block.Text.ShouldBe("response text");
    }

    [Fact]
    public void AssistantThinkingBlock_WithSignature_PreservesSignature()
    {
        var block = new ThinkingContent("reasoning", "sig-abc");

        block.Thinking.ShouldBe("reasoning");
        block.ThinkingSignature.ShouldBe("sig-abc");
    }

    [Fact]
    public void ToolUseBlock_HasIdNameInput()
    {
        var args = new Dictionary<string, object?> { ["query"] = "test" };
        var block = new ToolCallContent("toolu_01", "search", args);

        block.Id.ShouldBe("toolu_01");
        block.Name.ShouldBe("search");
        block.Arguments.ShouldContainKey("query");
    }

    [Fact]
    public void ToolResultMessage_HasToolCallId()
    {
        var msg = new ToolResultMessage(
            ToolCallId: "toolu_01",
            ToolName: "search",
            Content: [new TextContent("results")],
            IsError: false,
            Timestamp: Ts);

        msg.ToolCallId.ShouldBe("toolu_01");
        msg.ToolName.ShouldBe("search");
    }

    [Fact]
    public void MultipleConsecutiveToolResults_CanBeMergedByProvider()
    {
        // The Anthropic provider merges consecutive tool results into a single user message.
        // Here we verify the data types support this pattern.
        var tr1 = new ToolResultMessage("tc1", "tool_a", [new TextContent("result 1")], false, Ts);
        var tr2 = new ToolResultMessage("tc2", "tool_b", [new TextContent("result 2")], false, Ts);

        var messages = new Message[] { tr1, tr2 };
        messages.Count().ShouldBe(2);
        messages.All(m => m is ToolResultMessage).ShouldBeTrue();
    }

    [Fact]
    public void SystemPrompt_CanBePlacedInContextField()
    {
        var context = TestHelpers.MakeContext("You are a coding assistant");

        context.SystemPrompt.ShouldBe("You are a coding assistant");
    }

    [Fact]
    public void ThinkingContent_RedactedBlock_HasRedactedFlag()
    {
        var block = new ThinkingContent("redacted data", Redacted: true);

        block.Redacted.ShouldBeTrue();
        block.Thinking.ShouldBe("redacted data");
    }
}
