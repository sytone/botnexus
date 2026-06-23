using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// Unit coverage for the shared Responses-API message/tool converter promoted from the inline
/// per-provider methods to Providers.Core (step 6/6 of #1377). The provider-level
/// <c>CopilotResponsesProviderParityTests</c> remain the byte-identical wire-contract safety net;
/// these tests pin the converter's contract directly.
/// </summary>
public class ResponsesMessageConverterTests
{
    private static LlmModel Model(params string[] input) => new(
        Id: "gpt-5",
        Name: "GPT-5",
        Api: "openai-responses",
        Provider: "openai",
        BaseUrl: "https://api.openai.com",
        Reasoning: true,
        Input: input.Length == 0 ? ["text"] : input,
        Cost: new ModelCost(1.0m, 2.0m, 0.5m, 1.5m),
        ContextWindow: 200000,
        MaxTokens: 16384);

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private const long Ts = 1_700_000_000_000L;

    [Fact]
    public void ConvertMessages_TextUserMessage_ProducesInputTextItem()
    {
        var messages = new Message[] { new UserMessage(new UserMessageContent("hello"), Ts) };

        var result = ResponsesMessageConverter.ConvertMessages(messages, Model());

        result.Count.ShouldBe(1);
        result[0]!["type"]!.GetValue<string>().ShouldBe("message");
        result[0]!["role"]!.GetValue<string>().ShouldBe("user");
        result[0]!["content"]![0]!["type"]!.GetValue<string>().ShouldBe("input_text");
        result[0]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("hello");
    }

    [Fact]
    public void ConvertMessages_ImageBlockOnNonVisionModel_IsFiltered()
    {
        var content = new UserMessageContent([
            new TextContent("look"),
            new ImageContent("AAAA", "image/png"),
        ]);
        var messages = new Message[] { new UserMessage(content, Ts) };

        var result = ResponsesMessageConverter.ConvertMessages(messages, Model("text"));

        var contentArray = result[0]!["content"]!.AsArray();
        contentArray.Count.ShouldBe(1);
        contentArray[0]!["type"]!.GetValue<string>().ShouldBe("input_text");
    }

    [Fact]
    public void ConvertMessages_ImageBlockOnVisionModel_IsKept()
    {
        var content = new UserMessageContent([
            new TextContent("look"),
            new ImageContent("AAAA", "image/png"),
        ]);
        var messages = new Message[] { new UserMessage(content, Ts) };

        var result = ResponsesMessageConverter.ConvertMessages(messages, Model("text", "image"));

        var contentArray = result[0]!["content"]!.AsArray();
        contentArray.Count.ShouldBe(2);
        contentArray[1]!["type"]!.GetValue<string>().ShouldBe("input_image");
        contentArray[1]!["image_url"]!.GetValue<string>().ShouldBe("data:image/png;base64,AAAA");
    }

    [Fact]
    public void ConvertMessages_AssistantText_EmitsOutputTextWithIndexedId()
    {
        var assistant = new AssistantMessage(
            Content: [new TextContent("answer")],
            Api: "openai-responses",
            Provider: "openai",
            ModelId: "gpt-5",
            Usage: Usage.Empty(),
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: null,
            Timestamp: Ts);

        var result = ResponsesMessageConverter.ConvertMessages([assistant], Model());

        result.Count.ShouldBe(1);
        result[0]!["type"]!.GetValue<string>().ShouldBe("message");
        result[0]!["role"]!.GetValue<string>().ShouldBe("assistant");
        result[0]!["status"]!.GetValue<string>().ShouldBe("completed");
        result[0]!["id"]!.GetValue<string>().ShouldBe("msg_0");
        result[0]!["content"]![0]!["type"]!.GetValue<string>().ShouldBe("output_text");
        result[0]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("answer");
    }

    [Fact]
    public void ConvertMessages_AssistantTextWithPhaseSignature_CarriesPhase()
    {
        var assistant = new AssistantMessage(
            Content: [new TextContent("thinking aloud") { TextSignature = """{"v":1,"id":"abc","phase":"commentary"}""" }],
            Api: "openai-responses",
            Provider: "openai",
            ModelId: "gpt-5",
            Usage: Usage.Empty(),
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: null,
            Timestamp: Ts);

        var result = ResponsesMessageConverter.ConvertMessages([assistant], Model());

        result[0]!["id"]!.GetValue<string>().ShouldBe("abc");
        result[0]!["phase"]!.GetValue<string>().ShouldBe("commentary");
    }

    [Fact]
    public void ConvertMessages_AssistantToolCall_EmitsFunctionCallWithSplitIds()
    {
        var assistant = new AssistantMessage(
            Content: [new ToolCallContent("call_1|fc_99", "do_thing", new Dictionary<string, object?> { ["x"] = 1 })],
            Api: "openai-responses",
            Provider: "openai",
            ModelId: "gpt-5",
            Usage: Usage.Empty(),
            StopReason: StopReason.ToolUse,
            ErrorMessage: null,
            ResponseId: null,
            Timestamp: Ts);
        // Pair the call with its result so the whole-transcript pairing pass keeps the function_call.
        var toolResult = new ToolResultMessage(
            ToolCallId: "call_1|fc_99", ToolName: "do_thing",
            Content: [new TextContent("ok")], IsError: false, Timestamp: Ts);

        var result = ResponsesMessageConverter.ConvertMessages([assistant, toolResult], Model());

        result[0]!["type"]!.GetValue<string>().ShouldBe("function_call");
        result[0]!["call_id"]!.GetValue<string>().ShouldBe("call_1");
        result[0]!["id"]!.GetValue<string>().ShouldBe("fc_99");
        result[0]!["name"]!.GetValue<string>().ShouldBe("do_thing");
    }

    [Fact]
    public void ConvertMessages_ToolResult_EmitsFunctionCallOutput()
    {
        // Pair the result with its originating call so the pairing pass keeps the function_call_output.
        var assistant = new AssistantMessage(
            Content: [new ToolCallContent("call_1|fc_99", "do_thing", new Dictionary<string, object?>())],
            Api: "openai-responses", Provider: "openai", ModelId: "gpt-5",
            Usage: Usage.Empty(), StopReason: StopReason.ToolUse,
            ErrorMessage: null, ResponseId: null, Timestamp: Ts);
        var toolResult = new ToolResultMessage(
            ToolCallId: "call_1|fc_99",
            ToolName: "do_thing",
            Content: [new TextContent("the result")],
            IsError: false,
            Timestamp: Ts);

        var result = ResponsesMessageConverter.ConvertMessages([assistant, toolResult], Model());

        var output = result.Single(n => n!["type"]!.GetValue<string>() == "function_call_output");
        output!["call_id"]!.GetValue<string>().ShouldBe("call_1");
        output!["output"]!.GetValue<string>().ShouldBe("the result");
    }

    [Fact]
    public void ConvertMessages_DanglingFunctionCallInOlderTurn_IsDropped()
    {
        // A tool-call in an OLDER (non-last) assistant turn whose tool-result was never written
        // (crash/abort two turns back, conversation continued). The Responses API rejects the
        // whole request (HTTP 400) when a function_call has no matching function_call_output, so
        // the converter must drop the unpaired call instead of emitting it verbatim.
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("first"), Ts),
            new AssistantMessage(
                Content: [new ToolCallContent("call_dangling|fc_1", "do_thing", new Dictionary<string, object?>())],
                Api: "openai-responses", Provider: "openai", ModelId: "gpt-5",
                Usage: Usage.Empty(), StopReason: StopReason.ToolUse,
                ErrorMessage: null, ResponseId: null, Timestamp: Ts),
            // No ToolResultMessage for call_dangling -- the turn was abandoned.
            new UserMessage(new UserMessageContent("second"), Ts + 1),
            new AssistantMessage(
                Content: [new TextContent("answer")],
                Api: "openai-responses", Provider: "openai", ModelId: "gpt-5",
                Usage: Usage.Empty(), StopReason: StopReason.Stop,
                ErrorMessage: null, ResponseId: null, Timestamp: Ts + 1),
        };

        var result = ResponsesMessageConverter.ConvertMessages(messages, Model());

        // The dangling function_call must NOT appear in the input array.
        result.Where(n => n!["type"]!.GetValue<string>() == "function_call").ShouldBeEmpty();
        // Everything else is preserved: 2 user messages + 1 assistant text.
        result.Count(n => n!["type"]!.GetValue<string>() == "message").ShouldBe(3);
    }

    [Fact]
    public void ConvertMessages_DanglingFunctionCallInLastTurn_IsDropped()
    {
        // The trailing assistant turn issued a tool-call but no result was ever written (the active
        // turn was aborted). On replay this dangling call would 400 the Responses request.
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("go"), Ts),
            new AssistantMessage(
                Content: [new ToolCallContent("call_last|fc_9", "do_thing", new Dictionary<string, object?>())],
                Api: "openai-responses", Provider: "openai", ModelId: "gpt-5",
                Usage: Usage.Empty(), StopReason: StopReason.ToolUse,
                ErrorMessage: null, ResponseId: null, Timestamp: Ts),
        };

        var result = ResponsesMessageConverter.ConvertMessages(messages, Model());

        result.Where(n => n!["type"]!.GetValue<string>() == "function_call").ShouldBeEmpty();
        result.Count.ShouldBe(1); // only the user message survives
    }

    [Fact]
    public void ConvertMessages_OrphanFunctionCallOutput_IsDropped()
    {
        // A tool-result with no prior tool-call (orphan) -- symmetric malformation. The Responses
        // API also rejects a function_call_output with no matching function_call.
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("hi"), Ts),
            new ToolResultMessage(
                ToolCallId: "call_orphan|fc_3", ToolName: "do_thing",
                Content: [new TextContent("orphan result")], IsError: false, Timestamp: Ts),
        };

        var result = ResponsesMessageConverter.ConvertMessages(messages, Model());

        result.Where(n => n!["type"]!.GetValue<string>() == "function_call_output").ShouldBeEmpty();
        result.Count.ShouldBe(1); // only the user message survives
    }

    [Fact]
    public void ConvertMessages_PairedToolCallAndResult_BothPreserved()
    {
        // Regression guard: a properly paired call + result must pass through untouched.
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("do it"), Ts),
            new AssistantMessage(
                Content: [new ToolCallContent("call_ok|fc_5", "do_thing", new Dictionary<string, object?>())],
                Api: "openai-responses", Provider: "openai", ModelId: "gpt-5",
                Usage: Usage.Empty(), StopReason: StopReason.ToolUse,
                ErrorMessage: null, ResponseId: null, Timestamp: Ts),
            new ToolResultMessage(
                ToolCallId: "call_ok|fc_5", ToolName: "do_thing",
                Content: [new TextContent("done")], IsError: false, Timestamp: Ts),
        };

        var result = ResponsesMessageConverter.ConvertMessages(messages, Model());

        var call = result.Single(n => n!["type"]!.GetValue<string>() == "function_call");
        call!["call_id"]!.GetValue<string>().ShouldBe("call_ok");
        var output = result.Single(n => n!["type"]!.GetValue<string>() == "function_call_output");
        output!["call_id"]!.GetValue<string>().ShouldBe("call_ok");
        output!["output"]!.GetValue<string>().ShouldBe("done");
    }

    [Fact]
    public void ConvertMessages_MixedPairedAndDangling_DropsOnlyUnpaired()
    {
        // One assistant turn emits two tool-calls; only the first gets a result. The paired call
        // and its output survive; the unpaired second call is dropped.
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("both"), Ts),
            new AssistantMessage(
                Content:
                [
                    new ToolCallContent("call_a|fc_a", "do_a", new Dictionary<string, object?>()),
                    new ToolCallContent("call_b|fc_b", "do_b", new Dictionary<string, object?>()),
                ],
                Api: "openai-responses", Provider: "openai", ModelId: "gpt-5",
                Usage: Usage.Empty(), StopReason: StopReason.ToolUse,
                ErrorMessage: null, ResponseId: null, Timestamp: Ts),
            new ToolResultMessage(
                ToolCallId: "call_a|fc_a", ToolName: "do_a",
                Content: [new TextContent("a done")], IsError: false, Timestamp: Ts),
        };

        var result = ResponsesMessageConverter.ConvertMessages(messages, Model());

        var calls = result.Where(n => n!["type"]!.GetValue<string>() == "function_call").ToList();
        calls.Count.ShouldBe(1);
        calls[0]!["call_id"]!.GetValue<string>().ShouldBe("call_a");
        result.Count(n => n!["type"]!.GetValue<string>() == "function_call_output").ShouldBe(1);
    }

    [Fact]
    public void ConvertTools_EmitsFunctionEntriesWithStrictFalse()
    {
        var tool = new Tool("read_file", "Read a file", Json("""{"type":"object","properties":{"p":{"type":"string"}}}"""));

        var result = ResponsesMessageConverter.ConvertTools([tool]);

        result.Count.ShouldBe(1);
        result[0]!["type"]!.GetValue<string>().ShouldBe("function");
        result[0]!["name"]!.GetValue<string>().ShouldBe("read_file");
        result[0]!["description"]!.GetValue<string>().ShouldBe("Read a file");
        result[0]!["strict"]!.GetValue<bool>().ShouldBeFalse();
    }
}
