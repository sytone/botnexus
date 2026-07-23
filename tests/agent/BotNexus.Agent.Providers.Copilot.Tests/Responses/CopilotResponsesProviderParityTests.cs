using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Copilot.Responses;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BotNexus.Agent.Providers.Copilot.Tests.Responses;

/// <summary>
/// Phase 2a — proves that the carved-out <see cref="CopilotResponsesProvider"/>
/// emits a byte-identical outbound request to the legacy path
/// (<see cref="OpenAIResponsesProvider"/> driven against a github-copilot model
/// with Bearer auth). This is the deployment-safety pivot for Phase 2b: when
/// the model registry flips gpt-5.x entries from <c>openai-responses</c> to
/// <c>github-copilot-responses</c>, the wire contract must not change.
/// </summary>
public class CopilotResponsesProviderParityTests
{
    [Fact]
    public void Api_IsGitHubCopilotResponses()
    {
        var provider = new CopilotResponsesProvider(
            new HttpClient(new NoOpHandler()),
            NullLogger<CopilotResponsesProvider>.Instance);
        provider.Api.ShouldBe("github-copilot-responses");
    }

    [Fact]
    public async Task Stream_Gpt55WithToolCatalogue_MatchesOpenAICopilotModeBody()
    {
        var (copilotHandler, openAiHandler) = await DriveBothProvidersAsync();

        var copilotBody = Normalise(copilotHandler.RequestBody!);
        var openAiBody = Normalise(openAiHandler.RequestBody!);

        copilotBody.ShouldBe(
            openAiBody,
            "CopilotResponsesProvider must emit a request body that is byte-identical to " +
            "OpenAIResponsesProvider's Copilot-mode output. A diff here will become a user-visible " +
            "behaviour change the moment BuiltInModels flips Copilot-flavour gpt-5.x entries to github-copilot-responses.");
    }

    [Fact]
    public async Task Stream_AppliesBearerAuth_AndCopilotDynamicHeaders()
    {
        var (handler, _) = await DriveBothProvidersAsync();

        handler.RequestHeaders.TryGetValue("Authorization", out var auth).ShouldBeTrue();
        auth.ShouldStartWith("Bearer ");

        // Two of the Copilot dynamic headers that are unconditionally applied to every
        // Copilot request (Copilot-Vision-Request is only set when images are present).
        handler.RequestHeaders.ShouldContainKey("X-Initiator");
        handler.RequestHeaders.ShouldContainKey("Openai-Intent");
    }

    [Fact]
    public async Task Stream_PostsToResponsesEndpoint()
    {
        var (handler, _) = await DriveBothProvidersAsync();
        handler.RequestUri!.AbsoluteUri.ShouldBe(
            "https://api.enterprise.githubcopilot.com/responses");
        handler.RequestMethod.ShouldBe("POST");
    }

    [Fact]
    public async Task Stream_Gpt56_RemovesCopilotChunkCrLf_WhilePreservingMarkdownNewlines()
    {
        const string sse =
            "event: response.output_item.added\n" +
            "data: {\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}\n\n" +
            "event: response.output_text.delta\n" +
            "data: {\"item_id\":\"msg_1\",\"delta\":\"\\r\\nK\"}\n\n" +
            "event: response.output_text.delta\n" +
            "data: {\"item_id\":\"msg_1\",\"delta\":\"\\r\\narthik\"}\n\n" +
            "event: response.output_text.delta\n" +
            "data: {\"item_id\":\"msg_1\",\"delta\":\"\\r\\n\\n\\n- first\\n- second\"}\n\n" +
            "event: response.output_item.done\n" +
            "data: {\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}\n\n" +
            "event: response.completed\n" +
            "data: {\"response\":{\"id\":\"resp_1\",\"status\":\"completed\"}}\n\n";

        var handler = new RecordingHandler(_ => SseResponse(sse));
        var provider = new CopilotResponsesProvider(
            new HttpClient(handler),
            NullLogger<CopilotResponsesProvider>.Instance);
        var model = BuildModel() with { Id = "gpt-5.6-sol", Name = "gpt-5.6-sol" };

        var result = await provider.Stream(
                model,
                BuildContext(),
                new CopilotResponsesOptions { ApiKey = "test-copilot-token" })
            .GetResultAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));

        var text = result.Content.OfType<TextContent>().Single().Text;
        text.ShouldBe("Karthik\n\n- first\n- second");
    }

    [Fact]
    public async Task Stream_Gpt56_RemovesRepeatedCopilotChunkCrLf_WithoutLosingTokenWhitespace()
    {
        // gpt-5.6-sol frames every token fragment with CRLF - sometimes more than one pair.
        // #2119: the single-pair SSE fix left the artifact when framing repeated, persisting
        // as one-token-per-line output. All leading CRLF pairs must be stripped while a
        // genuine token-leading space and LF Markdown boundaries survive.
        const string sse =
            "event: response.output_item.added\n" +
            "data: {\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}\n\n" +
            "event: response.output_text.delta\n" +
            "data: {\"item_id\":\"msg_1\",\"delta\":\"\\r\\n\\r\\nUnder\"}\n\n" +
            "event: response.output_text.delta\n" +
            "data: {\"item_id\":\"msg_1\",\"delta\":\"\\r\\n\\r\\nstood\"}\n\n" +
            "event: response.output_text.delta\n" +
            "data: {\"item_id\":\"msg_1\",\"delta\":\"\\r\\n\\r\\n now\"}\n\n" +
            "event: response.output_item.done\n" +
            "data: {\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}\n\n" +
            "event: response.completed\n" +
            "data: {\"response\":{\"id\":\"resp_1\",\"status\":\"completed\"}}\n\n";

        var handler = new RecordingHandler(_ => SseResponse(sse));
        var provider = new CopilotResponsesProvider(
            new HttpClient(handler),
            NullLogger<CopilotResponsesProvider>.Instance);
        var model = BuildModel() with { Id = "gpt-5.6-sol", Name = "gpt-5.6-sol" };

        var result = await provider.Stream(
                model,
                BuildContext(),
                new CopilotResponsesOptions { ApiKey = "test-copilot-token" })
            .GetResultAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));

        result.Content.OfType<TextContent>().Single().Text.ShouldBe("Understood now");
    }

    [Fact]
    public async Task Stream_PreGpt56_PreservesLeadingCrLfVerbatim()
    {
        const string sse =
            "event: response.output_text.delta\n" +
            "data: {\"item_id\":\"msg_1\",\"delta\":\"\\r\\nintentional\"}\n\n" +
            "event: response.completed\n" +
            "data: {\"response\":{\"id\":\"resp_1\",\"status\":\"completed\"}}\n\n";

        var handler = new RecordingHandler(_ => SseResponse(sse));
        var provider = new CopilotResponsesProvider(
            new HttpClient(handler),
            NullLogger<CopilotResponsesProvider>.Instance);

        var result = await provider.Stream(
                BuildModel(),
                BuildContext(),
                new CopilotResponsesOptions { ApiKey = "test-copilot-token" })
            .GetResultAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));

        result.Content.OfType<TextContent>().Single().Text.ShouldBe("\r\nintentional");
    }

    private static async Task<(RecordingHandler CopilotHandler, RecordingHandler OpenAIHandler)> DriveBothProvidersAsync()
    {
        var copilotHandler = new RecordingHandler(_ => SseResponse(MinimalSse));
        var copilotProvider = new CopilotResponsesProvider(
            new HttpClient(copilotHandler),
            NullLogger<CopilotResponsesProvider>.Instance);

        var openAiHandler = new RecordingHandler(_ => SseResponse(MinimalSse));
        var openAiProvider = new OpenAIResponsesProvider(
            new HttpClient(openAiHandler),
            NullLogger<OpenAIResponsesProvider>.Instance);

        var model = BuildModel();
        var context = BuildContext();

        var copilotOpts = new CopilotResponsesOptions
        {
            ApiKey = "test-copilot-token",
            MaxTokens = 4096,
            CacheRetention = CacheRetention.Short,
        };
        var openAiOpts = new OpenAIResponsesOptions
        {
            ApiKey = "test-copilot-token",
            MaxTokens = 4096,
            CacheRetention = CacheRetention.Short,
        };

        var copilotStream = copilotProvider.Stream(model, context, copilotOpts);
        _ = await copilotStream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var openAiStream = openAiProvider.Stream(model, context, openAiOpts);
        _ = await openAiStream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        return (copilotHandler, openAiHandler);
    }

    // NB: Api stays "openai-responses" intentionally — Phase 2a is additive and the
    // built-in routing has not flipped yet. The parity proof pins the wire contract
    // *as it exists today* so Phase 2b can change BuiltInModels without drift.
    private static LlmModel BuildModel() => new(
        Id: "gpt-5.5",
        Name: "gpt-5.5",
        Api: "openai-responses",
        Provider: "github-copilot",
        BaseUrl: "https://api.enterprise.githubcopilot.com",
        Reasoning: true,
        Input: ["text", "image"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 200000,
        MaxTokens: 16384);

    private static Context BuildContext()
    {
        const long fixedTimestamp = 1_700_000_000_000L;
        return new Context(
            SystemPrompt: "You are a helpful assistant operating inside BotNexus tests. " +
                          "Reply concisely and use tools when asked.",
            Messages:
            [
                new UserMessage(new UserMessageContent("List the first three prime numbers."), fixedTimestamp),
            ],
            Tools:
            [
                new Tool(
                    Name: "list_primes",
                    Description: "Returns the first N prime numbers.",
                    Parameters: JsonDocument.Parse("""
                        {
                          "type": "object",
                          "properties": {
                            "count": { "type": "integer", "minimum": 1, "maximum": 100 }
                          },
                          "required": ["count"]
                        }
                        """).RootElement.Clone()),
            ]);
    }

    private const string MinimalSse =
        "event: response.created\n" +
        "data: {\"response\":{\"id\":\"resp_fixture000000000001\"}}\n" +
        "\n" +
        "event: response.completed\n" +
        "data: {\"response\":{\"id\":\"resp_fixture000000000001\",\"status\":\"completed\",\"usage\":{\"input_tokens\":10,\"output_tokens\":5,\"total_tokens\":15}}}\n" +
        "\n";

    private static string Normalise(string text)
        => text.Replace("\r\n", "\n").TrimEnd();

    private static HttpResponseMessage SseResponse(string payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream"),
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public string? RequestMethod { get; private set; }
        public string? RequestBody { get; private set; }
        public Uri? RequestUri { get; private set; }
        public Dictionary<string, string> RequestHeaders { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestMethod = request.Method.Method;
            RequestUri = request.RequestUri;
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

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
