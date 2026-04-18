using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.Providers.Core.Tests.Models;

public class MessageTests
{
    private static readonly long TestTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public void UserMessage_WithStringContent_SetsProperties()
    {
        var msg = new UserMessage(new UserMessageContent("hello"), TestTimestamp);

        msg.Content.IsText.Should().BeTrue();
        msg.Content.Text.Should().Be("hello");
        msg.Timestamp.Should().Be(TestTimestamp);
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

        msg.Content.IsText.Should().BeFalse();
        msg.Content.Blocks.Should().HaveCount(2);
        msg.Content.Blocks![0].Should().BeOfType<TextContent>();
        msg.Content.Blocks[1].Should().BeOfType<ImageContent>();
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

        msg.Content.Should().HaveCount(2);
        msg.Api.Should().Be("openai-completions");
        msg.Provider.Should().Be("openai");
        msg.ModelId.Should().Be("gpt-4o");
        msg.StopReason.Should().Be(StopReason.Stop);
        msg.ErrorMessage.Should().BeNull();
        msg.ResponseId.Should().Be("resp-1");
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

        msg.IsError.Should().BeTrue();
        msg.ToolCallId.Should().Be("tc-1");
        msg.ToolName.Should().Be("read_file");
        msg.Content.Should().HaveCount(1);
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

        msg.IsError.Should().BeFalse();
    }

    [Fact]
    public void Usage_Empty_HasZeroCost()
    {
        var usage = Usage.Empty();

        usage.Input.Should().Be(0);
        usage.Output.Should().Be(0);
        usage.CacheRead.Should().Be(0);
        usage.CacheWrite.Should().Be(0);
        usage.TotalTokens.Should().Be(0);
        usage.Cost.Total.Should().Be(0);
    }

    [Fact]
    public void UsageCost_CalculatesTotal()
    {
        var cost = new UsageCost(0.01m, 0.02m, 0.001m, 0.005m, 0.036m);

        cost.Input.Should().Be(0.01m);
        cost.Output.Should().Be(0.02m);
        cost.Total.Should().Be(0.036m);
    }

    [Fact]
    public void Message_Serialization_PreservesDiscriminator()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        Message original = new UserMessage(new UserMessageContent("test"), TestTimestamp);

        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<Message>(json, options);

        deserialized.Should().BeOfType<UserMessage>();
    }
}
