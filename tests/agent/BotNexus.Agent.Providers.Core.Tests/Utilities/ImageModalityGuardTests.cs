using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Diagnostics;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Core.Tests.Utilities;

/// <summary>
/// Coverage for #2485: image content parts must never be discarded silently when the resolved
/// model does not declare the <c>image</c> input modality. The drop itself is retained (a hard
/// failure would break every currently-working text-only setup); what is asserted here is that the
/// drop is reported with a specific, structured warning naming the model and the dropped count, and
/// that a vision-capable model still passes images through untouched.
/// </summary>
[Collection(BotNexus.Agent.Providers.Core.Tests.Diagnostics.ProviderDiagnosticsCollection.Name)]
public class ImageModalityGuardTests : IDisposable
{
    private readonly RecordingLoggerFactory _factory = new();

    public ImageModalityGuardTests()
    {
        ProviderDiagnostics.LoggerFactory = _factory;
    }

    public void Dispose()
    {
        ProviderDiagnostics.LoggerFactory = null!;
        GC.SuppressFinalize(this);
    }

    private const long Ts = 1_700_000_000_000L;

    private static LlmModel Model(string api, params string[] input) => new(
        Id: "test-model-1",
        Name: "Test Model",
        Api: api,
        Provider: "test-provider",
        BaseUrl: "https://example.invalid",
        Reasoning: false,
        Input: input.Length == 0 ? ["text"] : input,
        Cost: new ModelCost(1.0m, 2.0m, 0.5m, 1.5m),
        ContextWindow: 100000,
        MaxTokens: 4096);

    private static UserMessage ImageUser(int imageCount)
    {
        var blocks = new List<ContentBlock> { new TextContent("look at these") };
        for (var i = 0; i < imageCount; i++)
            blocks.Add(new ImageContent($"DATA{i}", "image/png"));
        return new UserMessage(new UserMessageContent(blocks), Ts);
    }

    private LogRecord SingleWarning()
    {
        var warnings = _factory.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        warnings.Count.ShouldBe(1, "exactly one drop warning expected; got: " +
            string.Join(" | ", _factory.Records.Select(r => $"{r.Level}:{r.Message}")));
        return warnings[0];
    }

    // ---- guard contract -------------------------------------------------

    [Fact]
    public void AllowImages_TextOnlyModel_ReturnsFalseAndWarnsWithModelAndCount()
    {
        var model = Model("openai-completions", "text");

        var allowed = ImageModalityGuard.AllowImages(model, 3, "unit.site");

        allowed.ShouldBeFalse();
        var record = SingleWarning();
        record.Message.ShouldContain("Dropping 3 image content part(s) at unit.site");
        record.Message.ShouldContain("model test-model-1");
        record.Message.ShouldContain("provider test-provider");
        record.Message.ShouldContain("api openai-completions");
        record.Message.ShouldContain("does not declare the 'image' input modality");
        record.State["DroppedImageCount"].ShouldBe("3");
        record.State["DropSite"].ShouldBe("unit.site");
        record.State["ModelId"].ShouldBe("test-model-1");
        record.State["Provider"].ShouldBe("test-provider");
        record.State["Api"].ShouldBe("openai-completions");
        record.State["ImageModality"].ShouldBe("image");
        record.State["DeclaredModalities"].ShouldBe("text");
    }

    [Fact]
    public void AllowImages_VisionModel_ReturnsTrueAndDoesNotWarn()
    {
        var model = Model("openai-completions", "text", "image");

        var allowed = ImageModalityGuard.AllowImages(model, 2, "unit.site");

        allowed.ShouldBeTrue();
        _factory.Records.Count(r => r.Level == LogLevel.Warning).ShouldBe(0);
    }

    [Fact]
    public void AllowImages_TextOnlyModelWithZeroImages_DoesNotWarn()
    {
        var model = Model("openai-completions", "text");

        var allowed = ImageModalityGuard.AllowImages(model, 0, "unit.site");

        allowed.ShouldBeFalse();
        _factory.Records.Count(r => r.Level == LogLevel.Warning).ShouldBe(0);
    }

    // ---- completions converter seam -------------------------------------

    [Fact]
    public void CompletionsConverter_UserImageOnTextOnlyModel_DropsAndWarnsWithSite()
    {
        var model = Model("openai-completions", "text");

        var result = CompletionsMessageConverter.Convert(
            null, model, [ImageUser(2)], new OpenAICompletionsCompat());

        var content = result[0]!["content"]!.AsArray();
        // #2485 AC4: the original text part PLUS the substituted user-visible drop notice. The
        // images themselves are still absent - the notice explains why.
        content.Count.ShouldBe(2);
        content[0]!["type"]!.GetValue<string>().ShouldBe("text");
        content[1]!["type"]!.GetValue<string>().ShouldBe("text");
        content[1]!["text"]!.GetValue<string>().ShouldContain("2 image attachment(s)");
        content[1]!["text"]!.GetValue<string>().ShouldContain("test-model-1");
        content.Select(n => n!["type"]!.GetValue<string>()).ShouldNotContain("image_url");

        var record = SingleWarning();
        record.State["DropSite"].ShouldBe("completions.user");
        record.State["DroppedImageCount"].ShouldBe("2");
        record.State["ModelId"].ShouldBe("test-model-1");
    }

    [Fact]
    public void CompletionsConverter_UserImageOnVisionModel_PassesThroughUntouched()
    {
        var model = Model("openai-completions", "text", "image");

        var result = CompletionsMessageConverter.Convert(
            null, model, [ImageUser(2)], new OpenAICompletionsCompat());

        var content = result[0]!["content"]!.AsArray();
        content.Count.ShouldBe(3);
        content[1]!["type"]!.GetValue<string>().ShouldBe("image_url");
        content[1]!["image_url"]!["url"]!.GetValue<string>().ShouldBe("data:image/png;base64,DATA0");
        content[2]!["image_url"]!["url"]!.GetValue<string>().ShouldBe("data:image/png;base64,DATA1");
        _factory.Records.Count(r => r.Level == LogLevel.Warning).ShouldBe(0);
    }

    [Fact]
    public void CompletionsConverter_ToolResultImageOnTextOnlyModel_DropsAndWarnsWithToolResultSite()
    {
        var model = Model("openai-completions", "text");
        // #3014: the transcript must carry the originating tool call, otherwise the tool result is an
        // orphan and is dropped by the shared MessageTransformer seam before the image guard is ever
        // reached. The fixture previously omitted it; the assertions below are unchanged in intent
        // (the tool-role message survives and the image is dropped with the tool-result drop site).
        var toolCall = new AssistantMessage(
            Content: [new ToolCallContent("call_1", "screenshot", new Dictionary<string, object?>())],
            Api: "openai-completions",
            Provider: "openai",
            ModelId: "gpt-4o",
            Usage: Usage.Empty(),
            StopReason: StopReason.ToolUse,
            ErrorMessage: null,
            ResponseId: null,
            Timestamp: Ts);
        var toolResult = new ToolResultMessage(
            ToolCallId: "call_1",
            ToolName: "screenshot",
            Content: [new TextContent("done"), new ImageContent("SHOT", "image/png")],
            IsError: false,
            Timestamp: Ts);

        var result = CompletionsMessageConverter.Convert(
            null, model, [toolCall, toolResult], new OpenAICompletionsCompat());

        result.Count(n => n!["role"]!.GetValue<string>() == "tool").ShouldBe(1);
        result.Last()!["role"]!.GetValue<string>().ShouldBe("tool");

        var record = SingleWarning();
        record.State["DropSite"].ShouldBe("completions.tool-result");
        record.State["DroppedImageCount"].ShouldBe("1");
    }

    // ---- responses converter seam ---------------------------------------

    [Fact]
    public void ResponsesConverter_UserImageOnTextOnlyModel_DropsAndWarnsWithSite()
    {
        var model = Model("openai-responses", "text");

        var result = ResponsesMessageConverter.ConvertMessages([ImageUser(1)], model);

        var content = result[0]!["content"]!.AsArray();
        // #2485 AC4: original text part plus the substituted notice; no input_image survives.
        content.Count.ShouldBe(2);
        content[0]!["type"]!.GetValue<string>().ShouldBe("input_text");
        content[1]!["type"]!.GetValue<string>().ShouldBe("input_text");
        content[1]!["text"]!.GetValue<string>().ShouldContain("1 image attachment(s)");
        content.Select(n => n!["type"]!.GetValue<string>()).ShouldNotContain("input_image");

        var record = SingleWarning();
        record.State["DropSite"].ShouldBe("responses.user");
        record.State["DroppedImageCount"].ShouldBe("1");
        record.State["Api"].ShouldBe("openai-responses");
    }

    // ---- AC4: the drop is visible to the USER, not only in the log ------

    [Fact]
    public void BuildDropNotice_TextOnlyModel_NamesCountModelAndProvider()
    {
        var model = Model("openai-completions", "text");

        var notice = ImageModalityGuard.BuildDropNotice(model, 3);

        notice.ShouldNotBeNull();
        notice.ShouldContain("3 image attachment(s)");
        notice.ShouldContain("were not delivered");
        notice.ShouldContain("test-model-1");
        notice.ShouldContain("test-provider");
        notice.ShouldContain("does not accept image input");
    }

    [Fact]
    public void BuildDropNotice_ZeroImages_ReturnsNullSoCleanRequestsAreUnchanged()
    {
        var model = Model("openai-completions", "text");

        ImageModalityGuard.BuildDropNotice(model, 0).ShouldBeNull();
    }

    [Fact]
    public void CompletionsConverter_VisionModel_EmitsNoDropNotice()
    {
        var model = Model("openai-completions", "text", "image");

        var result = CompletionsMessageConverter.Convert(
            null, model, [ImageUser(2)], new OpenAICompletionsCompat());

        var texts = result[0]!["content"]!.AsArray()
            .Where(n => n!["type"]!.GetValue<string>() == "text")
            .Select(n => n!["text"]!.GetValue<string>());
        texts.ShouldNotContain(t => t.Contains("were not delivered", StringComparison.Ordinal));
    }

    [Fact]
    public void ResponsesConverter_VisionModel_EmitsNoDropNotice()
    {
        var model = Model("openai-responses", "text", "image");

        var result = ResponsesMessageConverter.ConvertMessages([ImageUser(1)], model);

        var texts = result[0]!["content"]!.AsArray()
            .Where(n => n!["type"]!.GetValue<string>() == "input_text")
            .Select(n => n!["text"]!.GetValue<string>());
        texts.ShouldNotContain(t => t.Contains("were not delivered", StringComparison.Ordinal));
    }

    [Fact]
    public void ResponsesConverter_UserImageOnVisionModel_PassesThroughUntouched()
    {
        var model = Model("openai-responses", "text", "image");

        var result = ResponsesMessageConverter.ConvertMessages([ImageUser(1)], model);

        var content = result[0]!["content"]!.AsArray();
        content.Count.ShouldBe(2);
        content[1]!["type"]!.GetValue<string>().ShouldBe("input_image");
        content[1]!["image_url"]!.GetValue<string>().ShouldBe("data:image/png;base64,DATA0");
        _factory.Records.Count(r => r.Level == LogLevel.Warning).ShouldBe(0);
    }

    private sealed record LogRecord(LogLevel Level, string Message, IReadOnlyDictionary<string, string> State);

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public List<LogRecord> Records { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Records);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(List<LogRecord> records) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var pair in pairs)
                        fields[pair.Key] = pair.Value?.ToString() ?? string.Empty;
                }

                lock (records)
                    records.Add(new LogRecord(logLevel, formatter(state, exception), fields));
            }
        }
    }
}
