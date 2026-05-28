using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.IntegrationMock.Tests;

public class IntegrationMockProviderTests
{
    private static Context UserContext(string text) => new(
        SystemPrompt: null,
        Messages: new[] { new UserMessage(new UserMessageContent(text), 0L) });

    private static LlmModel Model(string baseUrl = "") => new(
        Id: IntegrationMockModels.DefaultModelId,
        Name: "Integration Mock Echo",
        Api: IntegrationMockModels.ApiName,
        Provider: IntegrationMockModels.ProviderName,
        BaseUrl: baseUrl,
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 128000,
        MaxTokens: 32000);

    [Fact]
    public async Task Stream_HelloWorld_ProducesExpectedEventsAndText()
    {
        var provider = new IntegrationMockProvider();

        var stream = provider.Stream(Model(), UserContext(DefaultCatalog.HelloWorldKey));

        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);

        var result = await stream.GetResultAsync();

        // First event is StartEvent, final is DoneEvent with Stop.
        events.First().ShouldBeOfType<StartEvent>();
        events.Last().ShouldBeOfType<DoneEvent>();
        ((DoneEvent)events.Last()).Reason.ShouldBe(StopReason.Stop);

        // Expect exactly one TextStartEvent and one TextEndEvent.
        events.OfType<TextStartEvent>().Count().ShouldBe(1);
        events.OfType<TextEndEvent>().Count().ShouldBe(1);

        // Combined deltas reconstruct the canonical message.
        var combined = string.Concat(events.OfType<TextDeltaEvent>().Select(d => d.Delta));
        combined.ShouldBe("Hello, world!");

        // Final result also exposes the text.
        result.Content.OfType<TextContent>().Single().Text.ShouldBe("Hello, world!");
        result.StopReason.ShouldBe(StopReason.Stop);
        result.Api.ShouldBe(IntegrationMockModels.ApiName);
        result.Provider.ShouldBe(IntegrationMockModels.ProviderName);
    }

    [Fact]
    public async Task Stream_UnknownKey_EmitsLoudNoScriptResponse()
    {
        var provider = new IntegrationMockProvider();

        var stream = provider.Stream(Model(), UserContext("UNKNOWN_KEY"));

        await foreach (var _ in stream) { }
        var result = await stream.GetResultAsync();

        result.StopReason.ShouldBe(StopReason.Stop);
        var text = result.Content.OfType<TextContent>().Single().Text;
        text.ShouldBe("NO_SCRIPT:UNKNOWN_KEY");
    }

    [Fact]
    public async Task Stream_HonoursDelaysBetweenSteps()
    {
        // Custom catalog with two ~80ms delays — total wall-clock must be at least ~150ms.
        var catalog = new MockCatalog(new Dictionary<string, IReadOnlyList<ScriptedResponseStep>>
        {
            ["DELAY"] = new List<ScriptedResponseStep>
            {
                new("text_delta", Delta: "A", DelayMs: 80),
                new("text_delta", Delta: "B", DelayMs: 80),
                new("text_end"),
                new("done")
            }
        });
        var provider = new IntegrationMockProvider(new MockCatalogLoader(catalog));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stream = provider.Stream(Model(), UserContext("DELAY"));
        await foreach (var _ in stream) { }
        await stream.GetResultAsync();
        sw.Stop();

        sw.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(140);
    }

    [Fact]
    public async Task Stream_ToolCallScript_EmitsToolCallEventsWithToolUseStop()
    {
        var catalog = new MockCatalog(new Dictionary<string, IReadOnlyList<ScriptedResponseStep>>
        {
            ["CALL_TOOL"] = new List<ScriptedResponseStep>
            {
                new("tool_call",
                    ToolName: "get_weather",
                    ToolArguments: new Dictionary<string, object?> { ["city"] = "Sydney" },
                    ToolCallId: "tc-1"),
                new("done", StopReason: "toolUse")
            }
        });
        var provider = new IntegrationMockProvider(new MockCatalogLoader(catalog));

        var stream = provider.Stream(Model(), UserContext("CALL_TOOL"));
        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);
        var result = await stream.GetResultAsync();

        events.OfType<ToolCallStartEvent>().Count().ShouldBe(1);
        var endEvt = events.OfType<ToolCallEndEvent>().Single();
        endEvt.ToolCall.Name.ShouldBe("get_weather");
        endEvt.ToolCall.Id.ShouldBe("tc-1");
        endEvt.ToolCall.Arguments["city"].ShouldBe("Sydney");

        result.StopReason.ShouldBe(StopReason.ToolUse);
        result.Content.OfType<ToolCallContent>().Single().Name.ShouldBe("get_weather");
    }

    [Fact]
    public async Task Stream_ErrorStep_EmitsErrorEvent()
    {
        var catalog = new MockCatalog(new Dictionary<string, IReadOnlyList<ScriptedResponseStep>>
        {
            ["BOOM"] = new List<ScriptedResponseStep>
            {
                new("error", ErrorMessage: "simulated failure")
            }
        });
        var provider = new IntegrationMockProvider(new MockCatalogLoader(catalog));

        var stream = provider.Stream(Model(), UserContext("BOOM"));
        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);
        var result = await stream.GetResultAsync();

        events.OfType<ErrorEvent>().Single().Error.ErrorMessage.ShouldBe("simulated failure");
        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldBe("simulated failure");
    }

    [Fact]
    public async Task Stream_CustomCatalogViaBaseUrl_OverridesDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mock-catalog-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path,
                """
                {
                  "scripts": {
                    "CUSTOM": [
                      { "type": "text_delta", "delta": "from-file" },
                      { "type": "text_end" },
                      { "type": "done" }
                    ]
                  }
                }
                """);

            var provider = new IntegrationMockProvider();
            var stream = provider.Stream(Model(baseUrl: path), UserContext("CUSTOM"));
            await foreach (var _ in stream) { }
            var result = await stream.GetResultAsync();

            result.Content.OfType<TextContent>().Single().Text.ShouldBe("from-file");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Stream_CustomCatalogMissingHelloWorld_StillServesBuiltInDefault()
    {
        // Custom catalog that does NOT define HELLO_WORLD — the default fallback must still apply.
        var catalog = new MockCatalog(new Dictionary<string, IReadOnlyList<ScriptedResponseStep>>
        {
            ["OTHER"] = new List<ScriptedResponseStep> { new("done") }
        });
        var provider = new IntegrationMockProvider(new MockCatalogLoader(catalog));

        var stream = provider.Stream(Model(), UserContext(DefaultCatalog.HelloWorldKey));
        await foreach (var _ in stream) { }
        var result = await stream.GetResultAsync();

        result.Content.OfType<TextContent>().Single().Text.ShouldBe("Hello, world!");
    }

    [Fact]
    public void ExtractKey_TrimmedLastUserMessageWins()
    {
        var context = new Context(
            SystemPrompt: null,
            Messages: new Message[]
            {
                new UserMessage(new UserMessageContent("FIRST"), 0L),
                new AssistantMessage(
                    Content: [new TextContent("noise")],
                    Api: "x", Provider: "x", ModelId: "x",
                    Usage: Usage.Empty(), StopReason: StopReason.Stop,
                    ErrorMessage: null, ResponseId: null, Timestamp: 0L),
                new UserMessage(new UserMessageContent("  HELLO_WORLD  "), 0L)
            });

        IntegrationMockProvider.ExtractKey(context).ShouldBe("HELLO_WORLD");
    }
}
