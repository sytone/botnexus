using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Copilot.Completions;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BotNexus.Agent.Providers.Copilot.Tests.Completions;

/// <summary>
/// Phase 2a — proves that the carved-out <see cref="CopilotCompletionsProvider"/>
/// emits a byte-identical outbound request to the legacy path
/// (<see cref="OpenAICompletionsProvider"/> driven against a github-copilot
/// model with Bearer auth). This is the deployment-safety pivot for Phase 2b:
/// when the model registry flips gemini/grok/gpt-4.x entries from
/// <c>openai-completions</c> to <c>github-copilot-completions</c>, the wire
/// contract must not change.
/// </summary>
public class CopilotCompletionsProviderParityTests
{
    [Fact]
    public void Api_IsGitHubCopilotCompletions()
    {
        var provider = new CopilotCompletionsProvider(
            new HttpClient(new NoOpHandler()),
            NullLogger<CopilotCompletionsProvider>.Instance);
        provider.Api.ShouldBe("github-copilot-completions");
    }

    [Fact]
    public async Task Stream_GeminiWithToolCatalogue_MatchesOpenAICopilotModeBody()
    {
        var (copilotHandler, openAiHandler) = await DriveBothProvidersAsync();

        var copilotBody = Normalise(copilotHandler.RequestBody!);
        var openAiBody = Normalise(openAiHandler.RequestBody!);

        copilotBody.ShouldBe(
            openAiBody,
            "CopilotCompletionsProvider must emit a request body that is byte-identical to " +
            "OpenAICompletionsProvider's Copilot-mode output. A diff here will become a user-visible " +
            "behaviour change the moment BuiltInModels flips Copilot-flavour completions entries to github-copilot-completions.");
    }

    [Fact]
    public async Task Stream_AppliesBearerAuth_AndCopilotDynamicHeaders()
    {
        var (handler, _) = await DriveBothProvidersAsync();

        handler.RequestHeaders.TryGetValue("Authorization", out var auth).ShouldBeTrue();
        auth.ShouldStartWith("Bearer ");

        handler.RequestHeaders.ShouldContainKey("X-Initiator");
        handler.RequestHeaders.ShouldContainKey("Openai-Intent");
    }

    [Fact]
    public async Task Stream_PostsToChatCompletionsEndpoint()
    {
        var (handler, _) = await DriveBothProvidersAsync();
        handler.RequestUri!.AbsoluteUri.ShouldBe(
            "https://api.enterprise.githubcopilot.com/chat/completions");
        handler.RequestMethod.ShouldBe("POST");
    }

    private static async Task<(RecordingHandler CopilotHandler, RecordingHandler OpenAIHandler)> DriveBothProvidersAsync()
    {
        var copilotHandler = new RecordingHandler(_ => SseResponse(MinimalSse));
        var copilotProvider = new CopilotCompletionsProvider(
            new HttpClient(copilotHandler),
            NullLogger<CopilotCompletionsProvider>.Instance);

        var openAiHandler = new RecordingHandler(_ => SseResponse(MinimalSse));
        var openAiProvider = new OpenAICompletionsProvider(
            new HttpClient(openAiHandler),
            NullLogger<OpenAICompletionsProvider>.Instance);

        var model = BuildModel();
        var context = BuildContext();

        var copilotOpts = new CopilotCompletionsOptions
        {
            ApiKey = "test-copilot-token",
            MaxTokens = 4096,
            CacheRetention = CacheRetention.Short,
        };
        var openAiOpts = new OpenAICompletionsOptions
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

    // NB: Api stays "openai-completions" intentionally — Phase 2a is additive and the
    // built-in routing has not flipped yet. The parity proof pins the wire contract
    // *as it exists today* so Phase 2b can change BuiltInModels without drift.
    private static LlmModel BuildModel() => new(
        Id: "gemini-2.5-pro",
        Name: "gemini-2.5-pro",
        Api: "openai-completions",
        Provider: "github-copilot",
        BaseUrl: "https://api.enterprise.githubcopilot.com",
        Reasoning: false,
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
        "data: {\"id\":\"chatcmpl_fixture\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}\n" +
        "\n" +
        "data: {\"id\":\"chatcmpl_fixture\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15}}\n" +
        "\n" +
        "data: [DONE]\n" +
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
