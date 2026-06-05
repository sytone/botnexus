using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Anthropic;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Copilot.Tests;

/// <summary>
/// Phase 0d of the Copilot provider carve-out (#810): gateway-level
/// provider-selection regression.
///
/// Where Phase 0a/0c pin the wire shape an individual provider emits,
/// Phase 0d pins the wiring that <i>chooses</i> the provider. It exercises
/// the full <see cref="ModelRegistry"/> → <see cref="LlmClient"/> →
/// <see cref="ApiProviderRegistry"/> → HTTP path so a regression in any of
/// (a) Copilot model registration, (b) provider registration, or
/// (c) <see cref="LlmClient.ResolveProvider"/> trips a failure here, not in
/// some downstream integration test.
///
/// When Phase 1 retargets Copilot models from <c>anthropic-messages</c> /
/// <c>openai-responses</c> / <c>openai-completions</c> onto dedicated
/// <c>github-copilot-*</c> APIs and registers a new <c>CopilotProvider</c>
/// trio, the assertions in <see cref="LlmClient_RoutesCopilotModel_ToCopilotEndpoint"/>
/// must keep passing — the user-visible outcome (request hits Copilot host
/// with Copilot integration headers and the right path) is invariant.
/// The non-Copilot rows in <see cref="LlmClient_RoutesDirectModel_ToVendorEndpoint"/>
/// must also stay green: direct Anthropic/OpenAI users cannot be impacted
/// by the carve-out.
/// </summary>
public class CopilotGatewayRoutingTests
{
    /// <summary>
    /// Per Copilot model family: assert (model.Api) registration is what we
    /// expect today, then drive LlmClient end-to-end and confirm the
    /// outbound request hit the Copilot host on the expected path with the
    /// Copilot integration headers attached.
    /// </summary>
    [Theory]
    [InlineData("claude-haiku-4.5", "anthropic-messages", "/v1/messages")]
    [InlineData("gpt-5.4", "openai-responses", "/responses")]
    [InlineData("gpt-4.1", "openai-completions", "/chat/completions")]
    public async Task LlmClient_RoutesCopilotModel_ToCopilotEndpoint(
        string modelId,
        string expectedApi,
        string expectedPath)
    {
        var (llmClient, model, handler) = BuildStack("github-copilot", modelId);

        model.Api.ShouldBe(expectedApi);
        model.BaseUrl.ShouldBe("https://api.individual.githubcopilot.com");

        await DriveStreamAsync(llmClient, model);

        handler.LastRequestUri.ShouldNotBeNull();
        handler.LastRequestUri!.Host.ShouldBe("api.individual.githubcopilot.com");
        handler.LastRequestUri.AbsolutePath.ShouldBe(expectedPath);
        handler.LastRequestUri.Scheme.ShouldBe("https");

        handler.RequestHeaders.ShouldContainKey("User-Agent");
        handler.RequestHeaders["User-Agent"].ShouldContain("GitHubCopilotChat");
        handler.RequestHeaders.ShouldContainKey("Editor-Version");
        handler.RequestHeaders.ShouldContainKey("Editor-Plugin-Version");
        handler.RequestHeaders.ShouldContainKey("Copilot-Integration-Id");
        handler.RequestHeaders["Copilot-Integration-Id"].ShouldContain("vscode-chat");

        handler.RequestHeaders.ShouldContainKey("Authorization");
        handler.RequestHeaders["Authorization"].ShouldStartWith("Bearer ");
    }

    /// <summary>
    /// Direct-vendor models (non-Copilot) must continue to route to the
    /// vendor host with vendor-appropriate auth, untouched by anything the
    /// Copilot carve-out does.
    /// </summary>
    [Theory]
    [InlineData("anthropic", "claude-sonnet-4-20250514", "anthropic-messages", "api.anthropic.com", "/v1/messages")]
    [InlineData("openai", "gpt-4.1", "openai-completions", "api.openai.com", "/v1/chat/completions")]
    [InlineData("openai", "o3", "openai-responses", "api.openai.com", "/v1/responses")]
    public async Task LlmClient_RoutesDirectModel_ToVendorEndpoint(
        string providerKey,
        string modelId,
        string expectedApi,
        string expectedHost,
        string expectedPath)
    {
        var (llmClient, model, handler) = BuildStack(providerKey, modelId);

        model.Api.ShouldBe(expectedApi);

        await DriveStreamAsync(llmClient, model);

        handler.LastRequestUri.ShouldNotBeNull();
        handler.LastRequestUri!.Host.ShouldBe(expectedHost);
        handler.LastRequestUri.AbsolutePath.ShouldBe(expectedPath);

        handler.RequestHeaders.ShouldNotContainKey("Copilot-Integration-Id");
    }

    private static (LlmClient Client, LlmModel Model, RecordingHandler Handler) BuildStack(string providerKey, string modelId)
    {
        var modelRegistry = new ModelRegistry();
        new BuiltInModels().RegisterAll(modelRegistry);

        var model = modelRegistry.GetModel(providerKey, modelId);
        model.ShouldNotBeNull($"BuiltInModels must register {providerKey}/{modelId}");

        var handler = new RecordingHandler(MakeResponseFactory());
        var httpClient = new HttpClient(handler);

        var apiProviders = new ApiProviderRegistry();
        apiProviders.Register(new AnthropicProvider(httpClient));
        apiProviders.Register(new OpenAIResponsesProvider(httpClient, NullLogger<OpenAIResponsesProvider>.Instance));
        apiProviders.Register(new OpenAICompletionsProvider(httpClient, NullLogger<OpenAICompletionsProvider>.Instance));

        var llmClient = new LlmClient(apiProviders, modelRegistry);
        return (llmClient, model!, handler);
    }

    private static async Task DriveStreamAsync(LlmClient client, LlmModel model)
    {
        var context = new Context(
            SystemPrompt: "tests",
            Messages: [new UserMessage(new UserMessageContent("ping"), 1_700_000_000_000L)]);

        var stream = client.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> MakeResponseFactory() => request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;

        // Each provider has its own SSE shape. The minimal "happy" stream
        // for each lets stream.GetResultAsync() complete without errors.
        if (path.EndsWith("/v1/messages", StringComparison.Ordinal))
        {
            return SseResponse("""
                event: message_start
                data: {"type":"message_start","message":{"id":"msg_1"}}

                event: message_stop
                data: {"type":"message_stop"}
                """);
        }

        if (path.EndsWith("/responses", StringComparison.Ordinal))
        {
            return SseResponse("""
                data: {"type":"response.completed","response":{"id":"resp_1","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"ok"}]}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}}
                data: [DONE]
                """);
        }

        // openai-completions
        return SseResponse("""
            data: {"id":"cmpl_1","choices":[{"delta":{"content":"ok"}}]}
            data: {"choices":[{"finish_reason":"stop","delta":{}}]}
            data: [DONE]
            """);
    };

    private static HttpResponseMessage SseResponse(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public Dictionary<string, string> RequestHeaders { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            RequestHeaders = request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(responseFactory(request));
        }
    }
}
