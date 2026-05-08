using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Anthropic;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

public class AnthropicProviderAlignmentTests
{
    [Fact]
    public async Task Stream_UsesModelMaxTokensDividedByThree_WhenOptionsMaxTokensNotProvided()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var httpClient = new HttpClient(handler);
        var provider = new AnthropicProvider(httpClient);
        var model = TestHelpers.MakeModel(maxTokens: 12000);
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestCount.ShouldBe(1);
        handler.RequestBody.ShouldNotBeNull();

        using var body = JsonDocument.Parse(handler.RequestBody!);
        body.RootElement.GetProperty("max_tokens").GetInt32().ShouldBe(model.MaxTokens / 3);
    }

    [Theory]
    [InlineData("refusal", StopReason.Refusal)]
    [InlineData("content_policy", StopReason.Sensitive)]
    [InlineData("pause_turn", StopReason.Stop)]
    [InlineData("sensitive", StopReason.Sensitive)]
    public async Task Stream_MapsAnthropicStopReasons(string anthropicReason, StopReason expected)
    {
        var payload = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"__STOP_REASON__"}}

            event: message_stop
            data: {"type":"message_stop"}
            """.Replace("__STOP_REASON__", anthropicReason, StringComparison.Ordinal);
        var handler = new RecordingHandler(_ => SseResponse(payload));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        result.StopReason.ShouldBe(expected);
    }

    [Fact]
    public async Task Stream_TracksCacheReadAndWriteTokensInUsage()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":11}}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5,"cache_read_input_tokens":7,"cache_creation_input_tokens":13}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        result.Usage.Input.ShouldBe(11);
        result.Usage.Output.ShouldBe(5);
        result.Usage.CacheRead.ShouldBe(7);
        result.Usage.CacheWrite.ShouldBe(13);
        result.Usage.TotalTokens.ShouldBe(36);
    }

    [Fact]
    public async Task Stream_UsesInjectedHttpClient()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestCount.ShouldBe(1);
        handler.LastRequestUri.ShouldNotBeNull();
        handler.LastRequestUri!.AbsoluteUri.ShouldBe($"{model.BaseUrl}/v1/messages");
    }

    [Fact]
    public async Task Stream_SkipsEmptyUserTextAndContentBlocks()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var context = new Context(
            SystemPrompt: "system",
            Messages:
            [
                new UserMessage(new UserMessageContent("   "), timestamp),
                new UserMessage(new UserMessageContent([new TextContent(""), new ImageContent("aGVsbG8=", "image/png")]), timestamp)
            ]);

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        messages.GetArrayLength().ShouldBe(1);
        messages[0].GetProperty("role").GetString().ShouldBe("user");
        messages[0].GetProperty("content").GetArrayLength().ShouldBe(1);
        messages[0].GetProperty("content")[0].GetProperty("type").GetString().ShouldBe("image");
    }

    [Fact]
    public async Task Stream_SkipsEmptyThinking_AndConvertsUnsignedThinkingToText()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var assistant = new AssistantMessage(
            Content:
            [
                new ThinkingContent("  ", "sig-empty"),
                new ThinkingContent("unsigned thinking", null),
                new ThinkingContent("signed thinking", "sig-123"),
                new ThinkingContent("redacted thinking", "", Redacted: true),
                new TextContent(""),
                new TextContent("visible")
            ],
            Api: model.Api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: "resp_1",
            Timestamp: timestamp);
        var context = new Context(
            SystemPrompt: "system",
            Messages: [new UserMessage(new UserMessageContent("hello"), timestamp), assistant]);

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var assistantMsg = body.RootElement.GetProperty("messages")[1];
        var content = assistantMsg.GetProperty("content");

        content.GetArrayLength().ShouldBe(3);
        content[0].GetProperty("type").GetString().ShouldBe("text");
        content[0].GetProperty("text").GetString().ShouldBe("unsigned thinking");
        content[1].GetProperty("type").GetString().ShouldBe("thinking");
        content[1].GetProperty("signature").GetString().ShouldBe("sig-123");
        content[2].GetProperty("type").GetString().ShouldBe("text");
        content[2].GetProperty("text").GetString().ShouldBe("visible");
    }

    [Fact]
    public async Task Stream_RedactedThinkingBlock_UsesThinkingSignatureAsData()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var assistant = new AssistantMessage(
            Content:
            [
                new ThinkingContent("should-never-be-sent", "sig-redacted", Redacted: true)
            ],
            Api: model.Api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: "resp_1",
            Timestamp: timestamp);
        var context = new Context(
            SystemPrompt: "system",
            Messages: [new UserMessage(new UserMessageContent("hello"), timestamp), assistant]);

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var redacted = body.RootElement.GetProperty("messages")[1].GetProperty("content")[0];

        redacted.GetProperty("data").GetString().ShouldBe("sig-redacted");
    }

    [Fact]
    public async Task Stream_ToolUseBlock_IncludesThoughtSignatureWhenPresent()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var assistant = new AssistantMessage(
            Content:
            [
                new ToolCallContent("toolu_01", "search", new Dictionary<string, object?> { ["query"] = "hello" }, "sig-tool")
            ],
            Api: model.Api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.ToolUse,
            ErrorMessage: null,
            ResponseId: "resp_1",
            Timestamp: timestamp);
        var context = new Context(
            SystemPrompt: "system",
            Messages: [new UserMessage(new UserMessageContent("hello"), timestamp), assistant]);

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var toolUse = body.RootElement.GetProperty("messages")[1].GetProperty("content")[0];
        toolUse.GetProperty("type").GetString().ShouldBe("tool_use");
        toolUse.GetProperty("signature").GetString().ShouldBe("sig-tool");
    }

    [Fact]
    public async Task Stream_ReasoningModelWithThinkingDisabled_SendsDisabledThinkingConfig()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel(reasoning: true);
        var context = TestHelpers.MakeContext();
        var options = new AnthropicOptions
        {
            ApiKey = "test-key",
            ThinkingEnabled = false
        };

        var stream = provider.Stream(model, context, options);
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        body.RootElement.GetProperty("thinking").GetProperty("type").GetString().ShouldBe("disabled");
    }

    [Fact]
    public async Task StreamSimple_WhenReasoningNotRequested_DisablesThinkingForReasoningModel()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel(reasoning: true);
        var context = TestHelpers.MakeContext();

        var stream = provider.StreamSimple(model, context, new SimpleStreamOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        body.RootElement.GetProperty("thinking").GetProperty("type").GetString().ShouldBe("disabled");
    }

    [Fact]
    public async Task Stream_AdaptiveModelWithBudgetWithoutEffort_UsesAdaptiveThinking()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel(id: "claude-sonnet-4.6", reasoning: true);
        var context = TestHelpers.MakeContext();
        var options = new AnthropicOptions
        {
            ApiKey = "test-key",
            ThinkingEnabled = true,
            ThinkingBudgetTokens = 2048
        };

        var stream = provider.Stream(model, context, options);
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        body.RootElement.GetProperty("thinking").GetProperty("type").GetString().ShouldBe("adaptive");
    }

    [Fact]
    public async Task Stream_ThinkingEnabled_SuppressesTemperature()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel(reasoning: false);
        var context = TestHelpers.MakeContext();
        var options = new AnthropicOptions
        {
            ApiKey = "test-key",
            ThinkingEnabled = true,
            Temperature = 0.7f
        };

        var stream = provider.Stream(model, context, options);
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        body.RootElement.TryGetProperty("temperature", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Stream_SetsAcceptAndDangerousDirectBrowserHeaders()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestHeaders.ShouldContainKey("accept");
        handler.RequestHeaders["accept"].ShouldContain("application/json");
        handler.RequestHeaders.ShouldContainKey("anthropic-dangerous-direct-browser-access");
        handler.RequestHeaders["anthropic-dangerous-direct-browser-access"].ShouldContain("true");
    }

    [Fact]
    public async Task Stream_CopilotHeadersUseOriginalMessagesForInitiator()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel(provider: "github-copilot");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var context = new Context(
            SystemPrompt: "system",
            Messages:
            [
                new UserMessage(new UserMessageContent("hello"), timestamp),
                new AssistantMessage(
                    Content: [new TextContent("failed")],
                    Api: model.Api,
                    Provider: model.Provider,
                    ModelId: model.Id,
                    Usage: Usage.Empty(),
                    StopReason: StopReason.Error,
                    ErrorMessage: "boom",
                    ResponseId: null,
                    Timestamp: timestamp)
            ]);

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestHeaders.ShouldContainKey("X-Initiator");
        handler.RequestHeaders["X-Initiator"].ShouldContain("agent");
    }

    [Fact]
    public async Task Stream_StringToolChoice_MapsToAnthropicObject()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();
        var options = new AnthropicOptions { ApiKey = "test-key", ToolChoice = "any" };

        var stream = provider.Stream(model, context, options);
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        body.RootElement.GetProperty("tool_choice").GetProperty("type").GetString().ShouldBe("any");
    }

    [Fact]
    public async Task Stream_ObjectToolChoice_PassesThroughWithoutRemapping()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();
        var options = new AnthropicOptions
        {
            ApiKey = "test-key",
            ToolChoice = new Dictionary<string, object?>
            {
                ["type"] = "auto",
                ["disable_parallel_tool_use"] = true
            }
        };

        var stream = provider.Stream(model, context, options);
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var toolChoice = body.RootElement.GetProperty("tool_choice");
        toolChoice.GetProperty("type").GetString().ShouldBe("auto");
        toolChoice.GetProperty("disable_parallel_tool_use").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Stream_UnpairedSurrogateInUserMessage_IsSanitizedBeforeRequest()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var context = new Context(
            SystemPrompt: "system",
            Messages: [new UserMessage(new UserMessageContent("hello \uD800 world"), timestamp)]);

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        body.RootElement.GetProperty("messages")[0]
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()
            .ShouldBe("hello  world");
    }

    [Fact]
    public async Task Stream_ToolUse_WithThoughtSignature_IncludesSignatureField()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var assistant = new AssistantMessage(
            Content:
            [
                new ToolCallContent("toolu_01", "search", new Dictionary<string, object?> { ["query"] = "test" }, "sig-tool-123")
            ],
            Api: model.Api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.ToolUse,
            ErrorMessage: null,
            ResponseId: "resp_1",
            Timestamp: timestamp);
        var context = new Context(
            SystemPrompt: "system",
            Messages: [new UserMessage(new UserMessageContent("hello"), timestamp), assistant]);

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var toolUse = body.RootElement.GetProperty("messages")[1].GetProperty("content")[0];
        toolUse.GetProperty("signature").GetString().ShouldBe("sig-tool-123");
    }

    [Fact]
    public async Task Stream_ToolUse_WithoutThoughtSignature_DoesNotIncludeSignatureField()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var assistant = new AssistantMessage(
            Content:
            [
                new ToolCallContent("toolu_01", "search", new Dictionary<string, object?> { ["query"] = "test" })
            ],
            Api: model.Api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.ToolUse,
            ErrorMessage: null,
            ResponseId: "resp_1",
            Timestamp: timestamp);
        var context = new Context(
            SystemPrompt: "system",
            Messages: [new UserMessage(new UserMessageContent("hello"), timestamp), assistant]);

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var toolUse = body.RootElement.GetProperty("messages")[1].GetProperty("content")[0];
        toolUse.TryGetProperty("signature", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("auto", "auto", null)]
    [InlineData("any", "any", null)]
    [InlineData("Read", "tool", "Read")]
    public async Task Stream_ToolChoice_StringValues_MapToAnthropicObjectSyntax(
        string configuredToolChoice,
        string expectedType,
        string? expectedName)
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();
        var options = new AnthropicOptions { ApiKey = "test-key" };
        SetToolChoice(options, configuredToolChoice);

        var stream = provider.Stream(model, context, options);
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var toolChoice = body.RootElement.GetProperty("tool_choice");
        toolChoice.GetProperty("type").GetString().ShouldBe(expectedType);
        if (expectedName is null)
        {
            toolChoice.TryGetProperty("name", out _).ShouldBeFalse();
        }
        else
        {
            toolChoice.GetProperty("name").GetString().ShouldBe(expectedName);
        }
    }

    [Fact]
    public async Task Stream_ToolChoice_ObjectValue_PassesThroughUnchanged()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();
        var options = new AnthropicOptions { ApiKey = "test-key" };
        var configured = new Dictionary<string, object?>
        {
            ["type"] = "auto",
            ["disable_parallel_tool_use"] = true
        };
        SetToolChoice(options, configured);

        var stream = provider.Stream(model, context, options);
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var toolChoice = body.RootElement.GetProperty("tool_choice");
        toolChoice.GetProperty("type").GetString().ShouldBe("auto");
        toolChoice.GetProperty("disable_parallel_tool_use").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Stream_ToolChoice_DictionaryWithMixedValues_PassesThroughUnchanged()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();
        var options = new AnthropicOptions { ApiKey = "test-key" };
        var configured = new Dictionary<string, object?>
        {
            ["type"] = "tool",
            ["name"] = "Read",
            ["disable_parallel_tool_use"] = true,
            ["extra"] = 123
        };
        SetToolChoice(options, configured);

        var stream = provider.Stream(model, context, options);
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var toolChoice = body.RootElement.GetProperty("tool_choice");
        toolChoice.GetProperty("type").GetString().ShouldBe("tool");
        toolChoice.GetProperty("name").GetString().ShouldBe("Read");
        toolChoice.GetProperty("disable_parallel_tool_use").GetBoolean().ShouldBeTrue();
        toolChoice.GetProperty("extra").GetInt32().ShouldBe(123);
    }

    [Fact]
    public async Task Stream_EmitsMessageStartBeforeContentAndToolCallStartBeforeEnd()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hello"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"tool_1","name":"read"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path\":\"README.md\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"}}

            event: message_stop
            data: {"type":"message_stop"}
            """));
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        var eventTypes = new List<string>();
        await foreach (var evt in stream)
        {
            eventTypes.Add(evt.Type);
        }

        eventTypes.IndexOf("start").ShouldBeLessThan(eventTypes.IndexOf("text_start"));
        eventTypes.IndexOf("text_start").ShouldBeLessThan(eventTypes.IndexOf("text_end"));
        eventTypes.IndexOf("toolcall_start").ShouldBeLessThan(eventTypes.IndexOf("toolcall_end"));
    }

    [Fact]
    public void Constructor_ThrowsWhenHttpClientIsNull()
    {
        var act = () => _ = new AnthropicProvider(null!);

        act.ShouldThrow<ArgumentNullException>()
            .ParamName.ShouldBe("httpClient");
    }

    private static HttpResponseMessage SseResponse(string payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };

    private static void SetToolChoice(AnthropicOptions options, object? value)
    {
        var property = typeof(AnthropicOptions).GetProperty("ToolChoice");
        property.ShouldNotBeNull();
        property!.SetValue(options, value);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? RequestBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public Dictionary<string, string> RequestHeaders { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            RequestHeaders = request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return responseFactory(request);
        }
    }
}
