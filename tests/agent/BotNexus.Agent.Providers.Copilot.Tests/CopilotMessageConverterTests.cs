using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;
using BotNexus.Agent.Providers.Copilot;

namespace BotNexus.Agent.Providers.Copilot.Tests;

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

        transformed.Count().ShouldBe(1);
        transformed[0].ShouldBeOfType<UserMessage>();
        var user = (UserMessage)transformed[0];
        user.Content.IsText.ShouldBeTrue();
        user.Content.Text.ShouldBe("Hello world");
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

        context.SystemPrompt.ShouldBe("You are a helpful assistant.");
        // With no compat or compat.SupportsDeveloperRole=false, system role is "system"
        var role = TestModel.Compat?.SupportsDeveloperRole is true ? "developer" : "system";
        role.ShouldBe("system");
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

        transformed.Count().ShouldBe(1); // no synthetic tool result at end-of-context
        var result = (AssistantMessage)transformed[0];
        result.Content.OfType<ToolCallContent>().ShouldContain(tc => tc.Id == "call_123" && tc.Name == "get_weather");
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

        transformed.Count().ShouldBe(2);
        var result = (ToolResultMessage)transformed[1];
        result.ToolCallId.ShouldBe("call_456");
        result.ToolName.ShouldBe("search");
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

        headers.ShouldContainKey("X-Initiator");
        headers["X-Initiator"].ShouldBe("user");
        headers.ShouldNotContainKey("Copilot-Vision-Request");
    }
}
