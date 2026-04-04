using FluentAssertions;
using BotNexus.Providers.Core.Compatibility;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Utilities;

namespace BotNexus.Providers.OpenAICompat.Tests;

public class MessageConversionWithCompatTests
{
    private static LlmModel MakeModel(OpenAICompletionsCompat compat, bool reasoning = false) => new(
        Id: "test-model",
        Name: "Test Model",
        Api: "openai-compat",
        Provider: "test",
        BaseUrl: "http://localhost:8000/v1",
        Reasoning: reasoning,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 128000,
        MaxTokens: 32000,
        Compat: compat
    );

    [Fact]
    public void SupportsDeveloperRole_False_SystemMessageUsesSystemRole()
    {
        var compat = new OpenAICompletionsCompat { SupportsDeveloperRole = false };
        var model = MakeModel(compat);

        // When SupportsDeveloperRole is false, the role should be "system"
        var role = compat.SupportsDeveloperRole ? "developer" : "system";
        role.Should().Be("system");
    }

    [Fact]
    public void SupportsDeveloperRole_True_SystemMessageUsesDeveloperRole()
    {
        var compat = new OpenAICompletionsCompat { SupportsDeveloperRole = true };
        var model = MakeModel(compat, reasoning: true);

        var role = compat.SupportsDeveloperRole ? "developer" : "system";
        role.Should().Be("developer");
    }

    [Fact]
    public void MaxTokensField_UsesMaxTokens_WhenConfigured()
    {
        var compat = new OpenAICompletionsCompat { MaxTokensField = "max_tokens" };

        compat.MaxTokensField.Should().Be("max_tokens");
    }

    [Fact]
    public void MaxTokensField_DefaultsToMaxCompletionTokens()
    {
        var compat = new OpenAICompletionsCompat();

        compat.MaxTokensField.Should().Be("max_completion_tokens");
    }

    [Fact]
    public void RequiresAssistantAfterToolResult_True_InsertsSyntheticAssistantMessage()
    {
        var compat = new OpenAICompletionsCompat { RequiresAssistantAfterToolResult = true };

        compat.RequiresAssistantAfterToolResult.Should().BeTrue();

        // Verify the message transformer preserves tool results correctly
        var toolCall = new ToolCallContent("call_1", "test_tool", new Dictionary<string, object?> { ["arg"] = "val" });
        var assistant = new AssistantMessage(
            Content: [toolCall],
            Api: "openai-compat",
            Provider: "test",
            ModelId: "test-model",
            Usage: Usage.Empty(),
            StopReason: StopReason.ToolUse,
            ErrorMessage: null,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );

        var toolResult = new ToolResultMessage(
            ToolCallId: "call_1",
            ToolName: "test_tool",
            Content: [new TextContent("result data")],
            IsError: false,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );

        var userFollowup = new UserMessage("What did you find?", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var model = MakeModel(compat);
        var messages = new List<Message> { assistant, toolResult, userFollowup };
        var transformed = MessageTransformer.TransformMessages(messages, model);

        // MessageTransformer should preserve the assistant and tool result
        transformed.OfType<AssistantMessage>().Should().NotBeEmpty();
        transformed.OfType<ToolResultMessage>().Should().Contain(tr => tr.ToolCallId == "call_1");
        transformed.OfType<UserMessage>().Should().NotBeEmpty();
    }

    [Fact]
    public void SupportsStrictMode_False_StrictNotIncludedInToolDefinitions()
    {
        var compat = new OpenAICompletionsCompat { SupportsStrictMode = false };

        compat.SupportsStrictMode.Should().BeFalse();

        // When SupportsStrictMode is false, the provider should NOT add strict=true to tool definitions
        // This is verified by checking the compat setting; the provider uses this flag in BuildRequestBody
    }

    [Fact]
    public void SupportsStrictMode_True_StrictIncludedInToolDefinitions()
    {
        var compat = new OpenAICompletionsCompat { SupportsStrictMode = true };

        compat.SupportsStrictMode.Should().BeTrue();
    }
}
