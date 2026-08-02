using BotNexus.Agent.Providers.Core.Diagnostics;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Core.Tests.Registry;

/// <summary>
/// Coverage for #2485 AC2/AC3: a re-registration that narrows a model's declared input modalities
/// must be reported, and a config-declared dynamic model must infer its input modalities from the
/// model family instead of being hardcoded to text-only. Both defects share one symptom - a
/// vision-capable model quietly becomes text-only and starts discarding images with no diagnostic
/// anywhere. These tests assert the observable diagnostic, not the internal boolean.
/// </summary>
[Collection(BotNexus.Agent.Providers.Core.Tests.Diagnostics.ProviderDiagnosticsCollection.Name)]
public class ModelModalityNarrowingTests : IDisposable
{
    private readonly RecordingLoggerFactory _factory = new();

    public ModelModalityNarrowingTests()
    {
        ProviderDiagnostics.LoggerFactory = _factory;
    }

    public void Dispose()
    {
        ProviderDiagnostics.LoggerFactory = null!;
        GC.SuppressFinalize(this);
    }

    private static LlmModel Model(string id, params string[] input) => new(
        Id: id,
        Name: id,
        Api: "openai-completions",
        Provider: "test-provider",
        BaseUrl: "https://example.invalid",
        Reasoning: false,
        Input: input.Length == 0 ? ["text"] : input,
        Cost: new ModelCost(1.0m, 2.0m, 0.5m, 1.5m),
        ContextWindow: 100000,
        MaxTokens: 4096);

    private List<LogRecord> Warnings() =>
        _factory.Records.Where(r => r.Level == LogLevel.Warning).ToList();

    [Fact]
    public void Register_WhenReRegistrationDropsImageModality_WarnsNamingBothModalitySets()
    {
        var registry = new ModelRegistry();
        registry.Register("test-provider", Model("vision-1", "text", "image"));

        registry.Register("test-provider", Model("vision-1", "text"));

        var warnings = Warnings();
        warnings.Count.ShouldBe(1, "exactly one narrowing warning expected; got: " +
            string.Join(" | ", _factory.Records.Select(r => $"{r.Level}:{r.Message}")));

        var warning = warnings[0];
        warning.State["ModelId"].ShouldBe("vision-1");
        warning.State["Provider"].ShouldBe("test-provider");
        warning.State["PreviousModalities"].ShouldBe("text,image");
        warning.State["NewModalities"].ShouldBe("text");
        warning.State["LostModalities"].ShouldBe("image");
        warning.Message.ShouldContain("vision-1");
        warning.Message.ShouldContain("image");
    }

    [Fact]
    public void Register_WhenReRegistrationNarrows_StillHonoursTheNarrowerEntry()
    {
        var registry = new ModelRegistry();
        registry.Register("test-provider", Model("vision-1", "text", "image"));
        registry.Register("test-provider", Model("vision-1", "text"));

        // The drop is legitimate - only the silence was the defect. The later entry still wins.
        registry.GetModel("test-provider", "vision-1")!.Input.ShouldBe(new[] { "text" });
    }

    [Fact]
    public void Register_WhenReRegistrationWidensModalities_IsSilent()
    {
        var registry = new ModelRegistry();
        registry.Register("test-provider", Model("vision-1", "text"));
        registry.Register("test-provider", Model("vision-1", "text", "image"));

        Warnings().ShouldBeEmpty();
    }

    [Fact]
    public void Register_WhenReRegistrationRepeatsSameModalities_IsSilent()
    {
        var registry = new ModelRegistry();
        registry.Register("test-provider", Model("vision-1", "text", "image"));
        registry.Register("test-provider", Model("vision-1", "text", "image"));

        Warnings().ShouldBeEmpty();
    }

    [Fact]
    public void Register_FirstRegistrationOfTextOnlyModel_IsSilent()
    {
        var registry = new ModelRegistry();
        registry.Register("test-provider", Model("text-only-1", "text"));

        Warnings().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("llava:13b")]
    [InlineData("qwen2.5-vl:7b")]
    [InlineData("llama3.2-vision")]
    [InlineData("claude-opus-4.6")]
    [InlineData("gpt-4o")]
    public void Infer_ForVisionFamily_DeclaresImageModality(string modelId)
    {
        DynamicModelCapabilities.Infer(modelId).Input.ShouldContain("image");
    }

    [Theory]
    [InlineData("llama3.2")]
    [InlineData("mistral-7b")]
    [InlineData("deepseek-coder")]
    public void Infer_ForNonVisionFamily_StaysTextOnly(string modelId)
    {
        DynamicModelCapabilities.Infer(modelId).Input.ShouldBe(new[] { "text" });
    }

    [Fact]
    public void Infer_ExplicitDeclarationBeatsInference()
    {
        // A config author pinning text-only on a vision family must get exactly text-only.
        DynamicModelCapabilities.Infer("llava:13b", declaredInput: ["text"])
            .Input.ShouldBe(new[] { "text" });

        // ...and pinning image on an unrecognised family must be honoured too.
        DynamicModelCapabilities.Infer("some-local-build", declaredInput: ["text", "image"])
            .Input.ShouldBe(new[] { "text", "image" });
    }

    [Fact]
    public void Infer_ExplicitDeclarationIsNormalisedAndAlwaysIncludesText()
    {
        DynamicModelCapabilities.Infer("some-local-build", declaredInput: ["  IMAGE ", "image"])
            .Input.ShouldBe(new[] { "text", "image" });
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
