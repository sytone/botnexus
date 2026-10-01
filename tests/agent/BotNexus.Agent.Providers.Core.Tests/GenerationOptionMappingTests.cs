using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Core.Tests;

public sealed class GenerationOptionMappingTests
{
    [Fact]
    public void StreamSimple_MapsSemanticAndExecutionOptions_ToProviderPrivateOptions()
    {
        SimpleStreamOptions? captured = null;
        var provider = new CapturingProvider(options => captured = options);
        var providers = new ApiProviderRegistry();
        providers.Register(provider);
        var client = new LlmClient(providers, new ModelRegistry());
        var model = new LlmModel("model", "Model", "test-api", "test-provider", "https://example.test", false, ["text"], new ModelCost(0, 0, 0, 0), 1000, 100);
        var cancellation = new CancellationTokenSource().Token;
        var payloadHook = new Func<object, LlmModel, Task<object?>>((payload, _) => Task.FromResult<object?>(payload));
        var semantic = new GenerationOptions
        {
            Temperature = 0.25f,
            MaxTokens = 321,
            ContextWindow = 456,
            CancellationToken = cancellation,
            CacheRetention = CacheRetention.Long,
            SessionId = "session-1",
            Reasoning = ThinkingLevel.High,
            ThinkingBudgets = new ThinkingBudgets { Minimal = 100, Low = 200, Medium = 300, High = 400 }
        };
        var execution = new ProviderExecutionOptions
        {
            ApiKey = "secret",
            Transport = Transport.WebSocket,
            Headers = new Dictionary<string, string> { ["x-test"] = "yes" },
            OnPayload = payloadHook,
            Metadata = new Dictionary<string, object> { ["trace"] = 42 },
            MaxRetryDelayMs = 2468,
            StreamSetupTimeoutMs = 1234,
            StreamIdleTimeoutMs = 5678
        };

        _ = client.StreamSimple(model, new Context(null, []), semantic, execution);

        captured.ShouldNotBeNull();
        captured.Temperature.ShouldBe(semantic.Temperature);
        captured.MaxTokens.ShouldBe(semantic.MaxTokens);
        captured.ContextWindow.ShouldBe(semantic.ContextWindow);
        captured.CancellationToken.ShouldBe(cancellation);
        captured.CacheRetention.ShouldBe(CacheRetention.Long);
        captured.SessionId.ShouldBe("session-1");
        captured.Reasoning.ShouldBe(ThinkingLevel.High);
        captured.ThinkingBudgets.ShouldBe(semantic.ThinkingBudgets);
        captured.ApiKey.ShouldBe("secret");
        captured.Transport.ShouldBe(Transport.WebSocket);
        captured.Headers?["x-test"].ShouldBe("yes");
        captured.OnPayload.ShouldBe(payloadHook);
        captured.Metadata?["trace"].ShouldBe(42);
        captured.MaxRetryDelayMs.ShouldBe(2468);
        captured.StreamSetupTimeoutMs.ShouldBe(1234);
        captured.StreamIdleTimeoutMs.ShouldBe(5678);

        captured.Headers.ShouldNotBeSameAs(execution.Headers);
        captured.Metadata.ShouldNotBeSameAs(execution.Metadata);
        captured.Headers!["x-test"] = "changed";
        captured.Metadata!["trace"] = 99;
        execution.Headers!["x-test"].ShouldBe("yes");
        execution.Metadata!["trace"].ShouldBe(42);
    }

    [Fact]
    public void StreamSimple_WithoutExecutionOptions_PreservesProviderDefaults()
    {
        SimpleStreamOptions? captured = null;
        var provider = new CapturingProvider(options => captured = options);
        var providers = new ApiProviderRegistry();
        providers.Register(provider);
        var client = new LlmClient(providers, new ModelRegistry());
        var model = new LlmModel("model", "Model", "test-api", "test-provider", "https://example.test", false, ["text"], new ModelCost(0, 0, 0, 0), 1000, 100);

        _ = client.StreamSimple(model, new Context(null, []), new GenerationOptions(), execution: null);

        captured.ShouldNotBeNull();
        captured.Transport.ShouldBe(Transport.Sse);
        captured.MaxRetryDelayMs.ShouldBe(60_000);
        captured.StreamSetupTimeoutMs.ShouldBe(0);
        captured.StreamIdleTimeoutMs.ShouldBeNull();
    }

    [Fact]
    public void GenerationOptions_HasNoProviderExecutionMembers()
    {
        var names = typeof(GenerationOptions).GetProperties().Select(property => property.Name).ToArray();
        foreach (var forbidden in new[] { "ApiKey", "Transport", "Headers", "OnPayload", "Metadata", "MaxRetryDelayMs", "StreamSetupTimeoutMs", "StreamIdleTimeoutMs" })
            names.ShouldNotContain(forbidden);
        typeof(GenerationOptions).GetProperties().Select(property => property.PropertyType).ShouldNotContain(typeof(Transport));
    }

    private sealed class CapturingProvider(Action<SimpleStreamOptions?> capture) : IApiProvider
    {
        public string Api => "test-api";
        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null) => new();
        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            capture(options);
            return new LlmStream();
        }
    }
}
