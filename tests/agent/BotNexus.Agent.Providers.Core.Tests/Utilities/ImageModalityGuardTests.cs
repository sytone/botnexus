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
        content.Count.ShouldBe(1);
        content[0]!["type"]!.GetValue<string>().ShouldBe("text");

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
        var toolResult = new ToolResultMessage(
            ToolCallId: "call_1",
            ToolName: "screenshot",
            Content: [new TextContent("done"), new ImageContent("SHOT", "image/png")],
            IsError: false,
            Timestamp: Ts);

        var result = CompletionsMessageConverter.Convert(
            null, model, [toolResult], new OpenAICompletionsCompat());

        result.Count.ShouldBe(1);
        result[0]!["role"]!.GetValue<string>().ShouldBe("tool");

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
        content.Count.ShouldBe(1);
        content[0]!["type"]!.GetValue<string>().ShouldBe("input_text");

        var record = SingleWarning();
        record.State["DropSite"].ShouldBe("responses.user");
        record.State["DroppedImageCount"].ShouldBe("1");
        record.State["Api"].ShouldBe("openai-responses");
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
