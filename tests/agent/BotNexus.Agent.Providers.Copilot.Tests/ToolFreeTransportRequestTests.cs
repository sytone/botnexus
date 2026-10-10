using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Anthropic;
using BotNexus.Agent.Providers.Copilot.Completions;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Copilot.Responses;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.OpenAI;
using BotNexus.Agent.Providers.OpenAICompat;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Copilot.Tests;

public sealed class ToolFreeTransportRequestTests
{
    public static TheoryData<string, bool, bool> DisableCases
    {
        get
        {
            var cases = new TheoryData<string, bool, bool>();
            foreach (var transport in Transports)
            foreach (var historyOnly in new[] { false, true })
            foreach (var simple in new[] { false, true })
                cases.Add(transport, historyOnly, simple);
            return cases;
        }
    }

    private static readonly string[] Transports =
    ["openai-completions", "openai-responses", "openai-compat", "anthropic-messages",
     "github-copilot-completions", "github-copilot-responses", "github-copilot-messages"];

    [Theory]
    [MemberData(nameof(DisableCases))]
    public async Task Stream_None_RemovesCatalogueAndHistoryFallbackAndOverridesForcedChoice(
        string transport, bool historyOnly, bool simple)
    {
        using var body = await CaptureAsync(transport, historyOnly, simple, disable: true);
        var root = body.RootElement;
        root.TryGetProperty("tools", out _).ShouldBeFalse();
        root.TryGetProperty("tool_stream", out _).ShouldBeFalse();
        var choice = root.GetProperty("tool_choice");
        if (transport.EndsWith("messages", StringComparison.Ordinal))
            choice.GetProperty("type").GetString().ShouldBe("none");
        else
            choice.GetString().ShouldBe("none");
        // Disabling future calls must not discard the completed call/result from history.
        var history = root.GetProperty(transport.EndsWith("responses", StringComparison.Ordinal) ? "input" : "messages").GetRawText();
        history.ShouldContain("previous-result");
        history.ShouldContain("forbidden");
    }

    [Theory]
    [InlineData("openai-completions", false)]
    [InlineData("openai-completions", true)]
    [InlineData("github-copilot-completions", false)]
    [InlineData("github-copilot-completions", true)]
    [InlineData("openai-compat", false)]
    [InlineData("openai-compat", true)]
    public async Task Stream_None_UnsupportedToolsOmitsEvenForcedToolChoice(string transport, bool historyOnly)
    {
        using var body = await CaptureAsync(transport, historyOnly, simple: false, disable: true, supportsTools: false);
        body.RootElement.TryGetProperty("tools", out _).ShouldBeFalse();
        body.RootElement.TryGetProperty("tool_stream", out _).ShouldBeFalse();
        body.RootElement.TryGetProperty("tool_choice", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("openai-completions")]
    [InlineData("github-copilot-completions")]
    [InlineData("openai-compat")]
    public async Task Stream_Default_HistoryWithoutCatalogueExercisesEmptyToolsFallback(string transport)
    {
        using var body = await CaptureAsync(transport, historyOnly: true, simple: false, disable: false);
        body.RootElement.GetProperty("tools").GetArrayLength().ShouldBe(0);
        body.RootElement.GetProperty("tool_choice").GetString().ShouldBe("required");
    }

    [Theory]
    [InlineData("openai-completions")]
    [InlineData("github-copilot-completions")]
    public async Task Stream_Default_ZaiCatalogueExercisesToolStreamFlag(string transport)
    {
        using var body = await CaptureAsync(transport, historyOnly: false, simple: false, disable: false);
        body.RootElement.GetProperty("tools").GetArrayLength().ShouldBe(1);
        body.RootElement.GetProperty("tool_stream").GetBoolean().ShouldBeTrue();
        body.RootElement.GetProperty("tool_choice").GetString().ShouldBe("required");
    }

    private static async Task<JsonDocument> CaptureAsync(
        string transport, bool historyOnly, bool simple, bool disable, bool supportsTools = true)
    {
        using var handler = new RecordingHandler(transport);
        using var client = new HttpClient(handler);
        IApiProvider provider = transport switch
        {
            "openai-completions" => new OpenAICompletionsProvider(client, NullLogger<OpenAICompletionsProvider>.Instance),
            "openai-responses" => new OpenAIResponsesProvider(client, NullLogger<OpenAIResponsesProvider>.Instance),
            "openai-compat" => new OpenAICompatProvider(client),
            "anthropic-messages" => new AnthropicProvider(client),
            "github-copilot-completions" => new CopilotCompletionsProvider(client, NullLogger<CopilotCompletionsProvider>.Instance),
            "github-copilot-responses" => new CopilotResponsesProvider(client, NullLogger<CopilotResponsesProvider>.Instance),
            "github-copilot-messages" => new CopilotMessagesProvider(client),
            _ => throw new ArgumentOutOfRangeException(nameof(transport))
        };
        var model = new LlmModel("test-model", "test-model", transport,
            transport.StartsWith("github-copilot", StringComparison.Ordinal) ? "github-copilot" : "openai",
            "https://provider.example/v1", false, ["text"], new ModelCost(0, 0, 0, 0), 32000, 4096,
            Compat: new OpenAICompletionsCompat { SupportsTools = supportsTools, ZaiToolStream = true });
        using var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
        var context = new Context("Answer without more calls",
        [
            new UserMessage(new UserMessageContent("look it up"), 1),
            new AssistantMessage([new ToolCallContent("call_1", "forbidden", new Dictionary<string, object?>())],
                transport, model.Provider, model.Id, Usage.Empty(), StopReason.ToolUse, null, null, 2),
            new ToolResultMessage("call_1", "forbidden", [new TextContent("previous-result")], false, 3)
        ], historyOnly ? [] : [new Tool("forbidden", "must not be advertised", schema.RootElement.Clone())]);
        ToolChoice? mode = disable ? ToolChoice.None : null;
        // Responses transports expose no provider-specific forced-choice option.
        StreamOptions options = transport switch
        {
            "openai-completions" => new OpenAICompletionsOptions { ApiKey = "test-key", ToolChoiceMode = mode, ToolChoice = "required" },
            "github-copilot-completions" => new CopilotCompletionsOptions { ApiKey = "test-key", ToolChoiceMode = mode, ToolChoice = "required" },
            "openai-compat" => new OpenAICompatOptions { ApiKey = "test-key", ToolChoiceMode = mode, ToolChoice = "required" },
            "anthropic-messages" => new AnthropicOptions { ApiKey = "test-key", ToolChoiceMode = mode, ToolChoice = new { type = "tool", name = "forbidden" } },
            "github-copilot-messages" => new CopilotMessagesOptions { ApiKey = "test-key", ToolChoiceMode = mode, ToolChoice = new { type = "tool", name = "forbidden" } },
            _ => new StreamOptions { ApiKey = "test-key", ToolChoiceMode = mode }
        };
        var stream = simple
            ? provider.StreamSimple(model, context, new SimpleStreamOptions { ApiKey = "test-key", ToolChoiceMode = mode })
            : provider.Stream(model, context, options);
        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        result.StopReason.ShouldBe(StopReason.Stop, result.ErrorMessage);
        return JsonDocument.Parse(handler.Body.ShouldNotBeNull());
    }

    private sealed class RecordingHandler(string transport) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content.ShouldNotBeNull().ReadAsStringAsync(cancellationToken);
            var sse = transport.EndsWith("messages", StringComparison.Ordinal)
                ? "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}\n\nevent: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
                : transport.EndsWith("responses", StringComparison.Ordinal)
                    ? "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}\n\ndata: [DONE]\n\n"
                    : "data: {\"id\":\"chat_1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"done\"},\"finish_reason\":null}]}\n\ndata: {\"id\":\"chat_1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
