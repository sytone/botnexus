using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Tests.Models;

public class MessageTests
{
    private static readonly long TestTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public void UserMessage_WithStringContent_SetsProperties()
    {
        var msg = new UserMessage(new UserMessageContent("hello"), TestTimestamp);

        msg.Content.IsText.ShouldBeTrue();
        msg.Content.Text.ShouldBe("hello");
        msg.Timestamp.ShouldBe(TestTimestamp);
    }

    [Fact]
    public void UserMessage_WithContentBlocks_SetsBlocks()
    {
        var blocks = new ContentBlock[]
        {
            new TextContent("hi"),
            new ImageContent("data", "image/png")
        };
        var msg = new UserMessage(new UserMessageContent(blocks), TestTimestamp);

        msg.Content.IsText.ShouldBeFalse();
        msg.Content.Blocks!.Count().ShouldBe(2);
        msg.Content.Blocks![0].ShouldBeOfType<TextContent>();
        msg.Content.Blocks[1].ShouldBeOfType<ImageContent>();
    }

    [Fact]
    public void AssistantMessage_WithMultipleContentBlocks_SetsAllProperties()
    {
        var content = new ContentBlock[]
        {
            new TextContent("answer"),
            new ToolCallContent("tc1", "tool", new Dictionary<string, object?>())
        };
        var usage = Usage.Empty();

        var msg = new AssistantMessage(
            Content: content,
            Api: "openai-completions",
            Provider: "openai",
            ModelId: "gpt-4o",
            Usage: usage,
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: "resp-1",
            Timestamp: TestTimestamp);

        msg.Content.Count().ShouldBe(2);
        msg.Api.ShouldBe("openai-completions");
        msg.Provider.ShouldBe("openai");
        msg.ModelId.ShouldBe("gpt-4o");
        msg.StopReason.ShouldBe(StopReason.Stop);
        msg.ErrorMessage.ShouldBeNull();
        msg.ResponseId.ShouldBe("resp-1");
    }

    [Fact]
    public void ToolResultMessage_WithError_SetsIsError()
    {
        var msg = new ToolResultMessage(
            ToolCallId: "tc-1",
            ToolName: "read_file",
            Content: [new TextContent("file not found")],
            IsError: true,
            Timestamp: TestTimestamp);

        msg.IsError.ShouldBeTrue();
        msg.ToolCallId.ShouldBe("tc-1");
        msg.ToolName.ShouldBe("read_file");
        msg.Content.Count().ShouldBe(1);
    }

    [Fact]
    public void ToolResultMessage_WithoutError_IsErrorFalse()
    {
        var msg = new ToolResultMessage(
            ToolCallId: "tc-2",
            ToolName: "list_dir",
            Content: [new TextContent("file1.cs")],
            IsError: false,
            Timestamp: TestTimestamp);

        msg.IsError.ShouldBeFalse();
    }

    [Fact]
    public void Usage_Empty_HasZeroCost()
    {
        var usage = Usage.Empty();

        usage.Input.ShouldBe(0);
        usage.Output.ShouldBe(0);
        usage.CacheRead.ShouldBe(0);
        usage.CacheWrite.ShouldBe(0);
        usage.TotalTokens.ShouldBe(0);
        usage.Cost.Total.ShouldBe(0);
    }

    [Fact]
    public void UsageCost_CalculatesTotal()
    {
        var cost = new UsageCost(0.01m, 0.02m, 0.001m, 0.005m, 0.036m);

        cost.Input.ShouldBe(0.01m);
        cost.Output.ShouldBe(0.02m);
        cost.Total.ShouldBe(0.036m);
    }

    [Fact]
    public void Message_Serialization_PreservesDiscriminator()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        Message original = new UserMessage(new UserMessageContent("test"), TestTimestamp);

        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<Message>(json, options);

        deserialized.ShouldBeOfType<UserMessage>();
    }
}
