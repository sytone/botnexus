using System.Net;
using System.Text;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.OpenAICompat;
using FluentAssertions;

namespace BotNexus.Providers.OpenAICompat.Tests;

public class OpenAICompatProviderConstructorTests
{
    [Fact]
    public async Task Stream_UsesInjectedHttpClient()
    {
        var handler = new RecordingHandler(_ => SseResponse("""
            data: {"id":"resp_1","choices":[{"delta":{"content":"hello"}}]}
            data: {"choices":[{"finish_reason":"stop","delta":{}}]}
            data: [DONE]
            """));
        var provider = new OpenAICompatProvider(new HttpClient(handler));
        var model = MakeModel();
        var context = MakeContext();

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(3));

        handler.RequestCount.Should().Be(1);
        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.AbsoluteUri.Should().Be($"{model.BaseUrl}/chat/completions");
    }

    [Fact]
    public void Constructor_ThrowsWhenHttpClientIsNull()
    {
        var act = () => _ = new OpenAICompatProvider(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("httpClient");
    }

    private static HttpResponseMessage SseResponse(string payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };

    private static LlmModel MakeModel() => new(
        Id: "compat-model",
        Name: "Compat",
        Api: "openai-compat",
        Provider: "custom",
        BaseUrl: "https://compat.example/v1",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 16384,
        MaxTokens: 4096);

    private static Context MakeContext() => new(
        SystemPrompt: "You are helpful",
        Messages: [new UserMessage(new UserMessageContent("hello"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            return Task.FromResult(responseFactory(request));
        }
    }
}
