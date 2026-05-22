using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.GitHubModels;

namespace BotNexus.Agent.Providers.GitHubModels.Tests;

/// <summary>
/// Unit tests for GitHubModelsProvider constructor and basic wiring.
/// </summary>
public class GitHubModelsProviderConstructorTests
{
    [Fact]
    public void Api_Returns_github_models()
    {
        var provider = new GitHubModelsProvider(new HttpClient(new NoOpHandler()));
        provider.Api.ShouldBe("github-models");
    }

    [Fact]
    public void Constructor_ThrowsWhenHttpClientIsNull()
    {
        var act = () => _ = new GitHubModelsProvider(null!);
        act.ShouldThrow<ArgumentNullException>()
            .ParamName.ShouldBe("httpClient");
    }

    [Fact]
    public async Task Stream_PostsToGitHubModelsEndpoint()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            data: {"id":"resp_1","choices":[{"delta":{"content":"hello"}}]}
            data: {"choices":[{"finish_reason":"stop","delta":{}}]}
            data: [DONE]
            """));

        var provider = new GitHubModelsProvider(new HttpClient(handler));
        var model = MakeModel();
        var context = MakeContext();

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-token" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.LastRequestUri.ShouldNotBeNull();
        handler.LastRequestUri!.AbsoluteUri.ShouldBe("https://models.inference.ai.azure.com/chat/completions");
    }

    [Fact]
    public async Task Stream_SendsBearerToken()
    {
        string? capturedAuth = null;
        var handler = new RecordingHandler(req =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            return SseResponse("""
                data: {"id":"resp_1","choices":[{"delta":{"content":"hi"}}]}
                data: {"choices":[{"finish_reason":"stop","delta":{}}]}
                data: [DONE]
                """);
        });

        var provider = new GitHubModelsProvider(new HttpClient(handler));
        var stream = provider.Stream(MakeModel(), MakeContext(), new StreamOptions { ApiKey = "ghp_token123" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        capturedAuth.ShouldBe("Bearer ghp_token123");
    }

    [Fact]
    public async Task Stream_UsesDeveloperRoleAsSystem()
    {
        string? capturedBody = null;
        var handler = new RecordingHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return SseResponse("""
                data: {"id":"resp_1","choices":[{"delta":{"content":"ok"}}]}
                data: {"choices":[{"finish_reason":"stop","delta":{}}]}
                data: [DONE]
                """);
        });

        var provider = new GitHubModelsProvider(new HttpClient(handler));
        var stream = provider.Stream(MakeModel(), MakeContext("You are a test assistant"), new StreamOptions { ApiKey = "tok" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        capturedBody.ShouldNotBeNull();
        capturedBody.ShouldContain("\"role\":\"system\"");
        capturedBody.ShouldNotContain("\"role\":\"developer\"");
    }

    private static HttpResponseMessage SseResponse(string payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };

    private static LlmModel MakeModel() => new(
        Id: "gpt-4o-mini",
        Name: "GPT-4o mini",
        Api: "github-models",
        Provider: "github-models",
        BaseUrl: "https://models.inference.ai.azure.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 128000,
        MaxTokens: 4096);

    private static Context MakeContext(string systemPrompt = "You are helpful") => new(
        SystemPrompt: systemPrompt,
        Messages: [new UserMessage(new UserMessageContent("hello"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> asyncFactory)
            : this(req => asyncFactory(req).GetAwaiter().GetResult()) { }

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]", Encoding.UTF8, "text/event-stream")
            });
    }
}
