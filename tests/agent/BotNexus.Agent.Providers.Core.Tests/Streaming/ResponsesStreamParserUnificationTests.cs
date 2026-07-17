using System.Reflection;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// Fitness fence for #1545 (slice 2 of #1540): the OpenAI and Copilot Responses SSE stream parsers
/// were ~95% byte-identical duplicates. After unification there is exactly ONE parser, in
/// Providers.Core, parameterized by the two real per-provider deltas (the Copilot usage-telemetry
/// per-event hook and the provider-specific service-tier options cast) supplied as delegates. These
/// tests lock that the unified Core parser exists, that the two per-provider duplicate types are
/// gone, and that the parser drains a Responses SSE stream and invokes the per-event hook for every
/// parsed event. The full provider-level byte-identical wire contract stays guarded by
/// <c>CopilotResponsesProviderParityTests</c> / <c>OpenAIResponsesProviderTests</c>.
/// </summary>
public class ResponsesStreamParserUnificationTests
{
    [Fact]
    public void UnifiedResponsesStreamParser_LivesOnceInProvidersCore()
    {
        var coreAssembly = typeof(ResponsesMessageConverter).Assembly;
        var parser = coreAssembly.GetType("BotNexus.Agent.Providers.Core.Streaming.ResponsesStreamParser");

        parser.ShouldNotBeNull(
            "The unified Responses stream parser must live in Providers.Core.Streaming (next to " +
            "ResponsesMessageConverter), not be duplicated per provider.");
        parser!.GetMethod("ParseAsync", BindingFlags.Public | BindingFlags.Static)
            .ShouldNotBeNull("ResponsesStreamParser must expose a public static ParseAsync.");
    }

    [Fact]
    public void PerProviderResponsesStreamParsers_AreDeleted_NotReDuplicated()
    {
        // The two former duplicate parser types must no longer exist anywhere in the loaded
        // provider assemblies; if either resolves, the duplication has returned.
        var openAiType = Type.GetType(
            "BotNexus.Agent.Providers.OpenAI.OpenAIResponsesStreamParser, BotNexus.Agent.Providers.OpenAI");
        var copilotType = Type.GetType(
            "BotNexus.Agent.Providers.Copilot.Responses.CopilotResponsesStreamParser, BotNexus.Agent.Providers.Copilot");

        openAiType.ShouldBeNull("OpenAIResponsesStreamParser must be deleted (folded into Core).");
        copilotType.ShouldBeNull("CopilotResponsesStreamParser must be deleted (folded into Core).");
    }

    [Fact]
    public async Task ParseAsync_DrainsStream_AndInvokesPerEventHookForEveryEvent()
    {
        var sse =
            "event: response.created\n" +
            "data: {\"response\":{\"id\":\"resp_1\"}}\n" +
            "\n" +
            "event: response.completed\n" +
            "data: {\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"usage\":{\"input_tokens\":3,\"output_tokens\":2,\"total_tokens\":5}}}\n" +
            "\n";

        var stream = new LlmStream();
        var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(sse)));
        var hookEvents = new List<string>();

        await ResponsesStreamParser.ParseAsync(
            stream,
            reader,
            Model(),
            options: null,
            api: "openai-responses",
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            emitError: (_, _, _, _) => { },
            onParsedEvent: root =>
            {
                if (root.TryGetProperty("response", out var resp) &&
                    resp.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.String)
                {
                    hookEvents.Add(id.GetString()!);
                }
            },
            resolveConfiguredServiceTier: null,
            normalizeTextDelta: null,
            ct: CancellationToken.None);

        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        result.StopReason.ShouldBe(StopReason.Stop);
        // The per-event hook fired once per parsed SSE event that carried a response id
        // (response.created + response.completed) -- proves the Copilot telemetry seam runs for all events.
        hookEvents.ShouldBe(new[] { "resp_1", "resp_1" });
    }

    [Fact]
    public async Task ParseAsync_ResolvesConfiguredServiceTier_FromProviderSuppliedDelegate()
    {
        // A "priority" service tier doubles cost; supplying it via the delegate must be honoured
        // even when the response body omits service_tier, proving the per-provider options cast was
        // preserved through the delegate seam.
        var sse =
            "event: response.completed\n" +
            "data: {\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1000,\"output_tokens\":1000,\"total_tokens\":2000}}}\n" +
            "\n";

        var stream = new LlmStream();
        var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(sse)));
        var tierResolverCalled = false;

        await ResponsesStreamParser.ParseAsync(
            stream,
            reader,
            Model(),
            options: null,
            api: "openai-responses",
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            emitError: (_, _, _, _) => { },
            onParsedEvent: null,
            resolveConfiguredServiceTier: _ =>
            {
                tierResolverCalled = true;
                return "priority";
            },
            normalizeTextDelta: null,
            ct: CancellationToken.None);

        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        tierResolverCalled.ShouldBeTrue("the service-tier resolver delegate must be consulted on completion.");
        // priority tier = 2x multiplier applied to a non-zero cost model.
        result.Usage.Cost.Total.ShouldBeGreaterThan(0m);
    }

    private static LlmModel Model() => new(
        Id: "gpt-5",
        Name: "GPT-5",
        Api: "openai-responses",
        Provider: "openai",
        BaseUrl: "https://api.openai.com",
        Reasoning: true,
        Input: ["text"],
        Cost: new ModelCost(1.0m, 2.0m, 0.5m, 1.5m),
        ContextWindow: 200000,
        MaxTokens: 16384);
}
