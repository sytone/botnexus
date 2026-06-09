using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

/// <summary>
/// Verifies that thinking content from non-Anthropic providers (OpenAI/Copilot)
/// is correctly filtered or converted when replayed to the Anthropic API.
/// Non-Anthropic signatures like "reasoning_content", "reasoning", "reasoning_text"
/// must not be sent as Anthropic thinking block signatures.
/// </summary>
public class CrossProviderThinkingFilterTests
{
    private static readonly long Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static readonly LlmModel Model = TestHelpers.MakeModel();

    private static AssistantMessage MakeAssistant(params ContentBlock[] blocks) => new(
        Content: blocks,
        Api: Model.Api,
        Provider: Model.Provider,
        ModelId: Model.Id,
        Usage: Usage.Empty(),
        StopReason: StopReason.Stop,
        ErrorMessage: null,
        ResponseId: "resp_test",
        Timestamp: Ts);

    [Theory]
    [InlineData("reasoning_content")]
    [InlineData("reasoning")]
    [InlineData("reasoning_text")]
    public void ThinkingBlock_WithNonAnthropicSignature_ConvertedToTextBlock(string foreignSignature)
    {
        // Arrange: simulate a session that started on OpenAI/Copilot and stored thinking
        // with a non-Anthropic signature field name
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("explain quantum computing"), Ts),
            MakeAssistant(
                new ThinkingContent("Let me think about quantum computing...", foreignSignature),
                new TextContent("Quantum computing uses qubits..."))
        };

        // Act: convert for Anthropic replay
        var result = AnthropicMessageConverter.ConvertMessages(messages, Model, isOAuthToken: false);

        // Assert: the thinking block should NOT be sent as a "thinking" type with the foreign signature
        var assistantMsg = result.First(m => m["role"]!.ToString() == "assistant");
        var blocks = (List<object>)assistantMsg["content"]!;

        // The foreign-signature thinking should become a text block, not a thinking block
        var thinkingBlocks = blocks
            .OfType<Dictionary<string, object?>>()
            .Where(b => b["type"]?.ToString() == "thinking")
            .ToList();
        thinkingBlocks.ShouldBeEmpty("Non-Anthropic thinking signatures must not produce thinking blocks");

        // The content should still be present as text
        var textBlocks = blocks
            .OfType<Dictionary<string, object?>>()
            .Where(b => b["type"]?.ToString() == "text")
            .ToList();
        textBlocks.Count.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void ThinkingBlock_WithAnthropicSignature_PreservedAsThinkingBlock()
    {
        // Arrange: a legitimate Anthropic thinking block with a proper signature
        // (Anthropic signatures are long base64-encoded strings, min ~100 chars)
        var anthropicSignature = "ErUBCkYIAxgCIkD" + new string('A', 200);

        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("think step by step"), Ts),
            MakeAssistant(
                new ThinkingContent("Let me reason...", anthropicSignature),
                new TextContent("Here is my answer..."))
        };

        // Act
        var result = AnthropicMessageConverter.ConvertMessages(messages, Model, isOAuthToken: false);

        // Assert: the thinking block should be preserved as-is
        var assistantMsg = result.First(m => m["role"]!.ToString() == "assistant");
        var blocks = (List<object>)assistantMsg["content"]!;

        var thinkingBlock = blocks
            .OfType<Dictionary<string, object?>>()
            .FirstOrDefault(b => b["type"]?.ToString() == "thinking");
        thinkingBlock.ShouldNotBeNull("Valid Anthropic thinking blocks must be preserved");
        thinkingBlock["signature"]!.ToString().ShouldBe(anthropicSignature);
    }

    [Fact]
    public void RedactedThinkingBlock_WithNonAnthropicSignature_IsDropped()
    {
        // Arrange: a "redacted" thinking block from a non-Anthropic provider
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("hello"), Ts),
            MakeAssistant(
                new ThinkingContent("reasoning_content", "reasoning_content", Redacted: true),
                new TextContent("The answer is 42"))
        };

        // Act
        var result = AnthropicMessageConverter.ConvertMessages(messages, Model, isOAuthToken: false);

        // Assert: no redacted_thinking block should be emitted
        var assistantMsg = result.First(m => m["role"]!.ToString() == "assistant");
        var blocks = (List<object>)assistantMsg["content"]!;

        var redactedBlocks = blocks
            .OfType<Dictionary<string, object?>>()
            .Where(b => b["type"]?.ToString() == "redacted_thinking")
            .ToList();
        redactedBlocks.ShouldBeEmpty("Non-Anthropic redacted thinking must not produce redacted_thinking blocks");
    }

    [Fact]
    public void ThinkingBlock_WithNullSignature_ConvertedToTextBlock()
    {
        // Arrange: existing behaviour -- null signature should produce text block
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("hello"), Ts),
            MakeAssistant(
                new ThinkingContent("Some reasoning without signature", null),
                new TextContent("Final answer"))
        };

        // Act
        var result = AnthropicMessageConverter.ConvertMessages(messages, Model, isOAuthToken: false);

        // Assert: should be text, not thinking
        var assistantMsg = result.First(m => m["role"]!.ToString() == "assistant");
        var blocks = (List<object>)assistantMsg["content"]!;

        var thinkingBlocks = blocks
            .OfType<Dictionary<string, object?>>()
            .Where(b => b["type"]?.ToString() == "thinking")
            .ToList();
        thinkingBlocks.ShouldBeEmpty();
    }
}
