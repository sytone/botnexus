using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using BotNexus.Providers.Core.Compatibility;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Utilities;
using BotNexus.Providers.Copilot;

namespace BotNexus.Providers.Copilot.Tests;

public class CopilotMessageConverterTests
{
    private static readonly LlmModel TestModel = new(
        Id: "gpt-4o",
        Name: "GPT-4o (Copilot)",
        Api: "github-copilot",
        Provider: "github-copilot",
        BaseUrl: "https://api.githubcopilot.com",
        Reasoning: false,
        Input: ["text", "image"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 128000,
        MaxTokens: 16384
    );

    [Fact]
    public void UserTextMessage_ConvertsToCorrectOpenAIFormat()
    {
        var messages = new List<Message>
        {
            new UserMessage(new UserMessageContent("Hello world"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        };

        var transformed = MessageTransformer.TransformMessages(messages, TestModel);

        transformed.Should().HaveCount(1);
        transformed[0].Should().BeOfType<UserMessage>();
        var user = (UserMessage)transformed[0];
        user.Content.IsText.Should().BeTrue();
        user.Content.Text.Should().Be("Hello world");
    }

    [Fact]
    public void SystemPrompt_PlacedCorrectly()
    {
        // The CopilotProvider uses "system" or "developer" role based on compat
        // Default model has no Compat set, so it falls back to "system"
        var context = new Context(
            SystemPrompt: "You are a helpful assistant.",
            Messages: [new UserMessage("Hi", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]
        );

        context.SystemPrompt.Should().Be("You are a helpful assistant.");
        // With no compat or compat.SupportsDeveloperRole=false, system role is "system"
        var role = TestModel.Compat?.SupportsDeveloperRole is true ? "developer" : "system";
        role.Should().Be("system");
    }

    [Fact]
    public void AssistantMessage_WithToolCalls_ConvertsFunctionFormat()
    {
        var toolCall = new ToolCallContent("call_123", "get_weather", new Dictionary<string, object?> { ["city"] = "London" });
        var assistant = new AssistantMessage(
            Content: [new TextContent("Let me check"), toolCall],
            Api: "github-copilot",
            Provider: "github-copilot",
            ModelId: "gpt-4o",
            Usage: Usage.Empty(),
            StopReason: StopReason.ToolUse,
            ErrorMessage: null,
            ResponseId: "resp_1",
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );

        var messages = new List<Message> { assistant };
        var transformed = MessageTransformer.TransformMessages(messages, TestModel);

        transformed.Should().HaveCount(2); // assistant + orphaned tool result
        var result = (AssistantMessage)transformed[0];
        result.Content.OfType<ToolCallContent>().Should().Contain(tc => tc.Id == "call_123" && tc.Name == "get_weather");
    }

    [Fact]
    public void ToolResult_ConvertsWithToolCallId()
    {
        var toolCall = new ToolCallContent("call_456", "search", new Dictionary<string, object?> { ["q"] = "test" });
        var assistant = new AssistantMessage(
            Content: [toolCall],
            Api: "github-copilot",
            Provider: "github-copilot",
            ModelId: "gpt-4o",
            Usage: Usage.Empty(),
            StopReason: StopReason.ToolUse,
            ErrorMessage: null,
            ResponseId: "resp_2",
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );

        var toolResult = new ToolResultMessage(
            ToolCallId: "call_456",
            ToolName: "search",
            Content: [new TextContent("Found 10 results")],
            IsError: false,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );

        var messages = new List<Message> { assistant, toolResult };
        var transformed = MessageTransformer.TransformMessages(messages, TestModel);

        transformed.Should().HaveCount(2);
        var result = (ToolResultMessage)transformed[1];
        result.ToolCallId.Should().Be("call_456");
        result.ToolName.Should().Be("search");
    }

    [Fact]
    public void CopilotHeaders_AreAddedWhenBuildingRequest()
    {
        var messages = new List<Message>
        {
            new UserMessage("Hello", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        };

        var hasImages = CopilotHeaders.HasVisionInput(messages);
        var headers = CopilotHeaders.BuildDynamicHeaders(messages, hasImages);

        headers.Should().ContainKey("X-Initiator");
        headers["X-Initiator"].Should().Be("user");
        headers.Should().NotContainKey("Copilot-Vision-Request");
    }
}
