using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using BotNexus.Providers.Copilot;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Tests.Unit.Tests;

public class HandlerRoutingTests
{
    [Theory]
    [InlineData("claude-opus-4.6", "/v1/messages")]
    [InlineData("claude-sonnet-4.6", "/v1/messages")]
    [InlineData("claude-sonnet-4.5", "/v1/messages")]
    public async Task CopilotProvider_RoutesClaudeModelsToAnthropicMessagesHandler(string modelId, string expectedPath)
    {
        var tokenStore = new InMemoryTokenStore(new OAuthToken("github-oauth-token", DateTimeOffset.UtcNow.AddMinutes(10)));
        string capturedPath = string.Empty;
        
        var providerHttpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                return JsonResponse("""{"token":"copilot-token","expires_at":9999999999}""");
            }
            
            capturedPath = request.RequestUri!.AbsolutePath;
            
            // Anthropic Messages format
            return JsonResponse("""
            {
              "content": [
                {
                  "type": "text",
                  "text": "Hello from Claude"
                }
              ],
              "stop_reason": "end_turn",
              "usage": { "input_tokens": 10, "output_tokens": 5 }
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.individual.githubcopilot.com")
        };

        var deviceFlow = BuildNoopDeviceFlow();
        var provider = new CopilotProvider(new CopilotConfig(), tokenStore, deviceFlow, NullLogger<CopilotProvider>.Instance, providerHttpClient);

        var response = await provider.ChatAsync(new ChatRequest(
            [new ChatMessage("user", "Hello")],
            new GenerationSettings { Model = modelId }));

        capturedPath.Should().Be(expectedPath);
        response.Content.Should().Be("Hello from Claude");
    }
    
    [Theory]
    [InlineData("gpt-4o", "/chat/completions")]
    [InlineData("gpt-4o-mini", "/chat/completions")]
    [InlineData("o1", "/chat/completions")]
    [InlineData("gemini-2.5-pro", "/chat/completions")]
    public async Task CopilotProvider_RoutesGPT4AndGeminiToOpenAiCompletionsHandler(string modelId, string expectedPath)
    {
        var tokenStore = new InMemoryTokenStore(new OAuthToken("github-oauth-token", DateTimeOffset.UtcNow.AddMinutes(10)));
        string capturedPath = string.Empty;
        
        var providerHttpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                return JsonResponse("""{"token":"copilot-token","expires_at":9999999999}""");
            }
            
            capturedPath = request.RequestUri!.AbsolutePath;
            
            // OpenAI Completions format
            return JsonResponse("""
            {
              "choices": [
                {
                  "message": { "role": "assistant", "content": "Hello from GPT" },
                  "finish_reason": "stop"
                }
              ],
              "usage": { "prompt_tokens": 10, "completion_tokens": 5 }
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.individual.githubcopilot.com")
        };

        var deviceFlow = BuildNoopDeviceFlow();
        var provider = new CopilotProvider(new CopilotConfig(), tokenStore, deviceFlow, NullLogger<CopilotProvider>.Instance, providerHttpClient);

        var response = await provider.ChatAsync(new ChatRequest(
            [new ChatMessage("user", "Hello")],
            new GenerationSettings { Model = modelId }));

        capturedPath.Should().Be(expectedPath);
        response.Content.Should().Be("Hello from GPT");
    }
    
    [Theory]
    [InlineData("gpt-5", "/v1/responses")]
    [InlineData("gpt-5.2", "/v1/responses")]
    [InlineData("gpt-5.4", "/v1/responses")]
    [InlineData("gpt-5.4-mini", "/v1/responses")]
    public async Task CopilotProvider_RoutesGPT5ToOpenAiResponsesHandler(string modelId, string expectedPath)
    {
        var tokenStore = new InMemoryTokenStore(new OAuthToken("github-oauth-token", DateTimeOffset.UtcNow.AddMinutes(10)));
        string capturedPath = string.Empty;
        
        var providerHttpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                return JsonResponse("""{"token":"copilot-token","expires_at":9999999999}""");
            }
            
            capturedPath = request.RequestUri!.AbsolutePath;
            
            // OpenAI Responses format - streaming events
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event: response.content_part.added\n" +
                    "data: {\"part\":{\"type\":\"text\",\"text\":\"Hello from GPT-5\"}}\n\n" +
                    "event: response.done\n" +
                    "data: {\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}}\n\n",
                    Encoding.UTF8, "text/event-stream")
            };
        }))
        {
            BaseAddress = new Uri("https://api.individual.githubcopilot.com")
        };

        var deviceFlow = BuildNoopDeviceFlow();
        var provider = new CopilotProvider(new CopilotConfig(), tokenStore, deviceFlow, NullLogger<CopilotProvider>.Instance, providerHttpClient);

        var response = await provider.ChatAsync(new ChatRequest(
            [new ChatMessage("user", "Hello")],
            new GenerationSettings { Model = modelId }));

        capturedPath.Should().Be(expectedPath);
        response.Content.Should().Be("Hello from GPT-5");
    }
    
    [Fact]
    public async Task CopilotProvider_UnknownModel_FallsBackToDefault()
    {
        var tokenStore = new InMemoryTokenStore(new OAuthToken("github-oauth-token", DateTimeOffset.UtcNow.AddMinutes(10)));
        
        var providerHttpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                return JsonResponse("""{"token":"copilot-token","expires_at":9999999999}""");
            }
            
            // Default is gpt-4o which uses OpenAI Completions format
            return JsonResponse("""
            {
              "choices": [
                {
                  "message": { "role": "assistant", "content": "Fallback response" },
                  "finish_reason": "stop"
                }
              ],
              "usage": { "prompt_tokens": 10, "completion_tokens": 5 }
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.individual.githubcopilot.com")
        };

        var deviceFlow = BuildNoopDeviceFlow();
        var provider = new CopilotProvider(new CopilotConfig(), tokenStore, deviceFlow, NullLogger<CopilotProvider>.Instance, providerHttpClient);

        var response = await provider.ChatAsync(new ChatRequest(
            [new ChatMessage("user", "Hello")],
            new GenerationSettings { Model = "unknown-model-xyz" }));

        response.Content.Should().Be("Fallback response");
    }
    
    private static GitHubDeviceCodeFlow BuildNoopDeviceFlow()
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            return JsonResponse("""{"access_token":"stub-token"}""");
        }));
        return new GitHubDeviceCodeFlow(httpClient);
    }
    
    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
    
    private class InMemoryTokenStore : IOAuthTokenStore
    {
        private readonly OAuthToken _token;
        
        public InMemoryTokenStore(OAuthToken token)
        {
            _token = token;
        }
        
        public Task<OAuthToken?> LoadTokenAsync(string providerKey, CancellationToken cancellationToken = default)
            => Task.FromResult<OAuthToken?>(_token);
        
        public Task SaveTokenAsync(string providerKey, OAuthToken token, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        
        public Task ClearTokenAsync(string providerKey, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
    
    private class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;
        
        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
        {
            _handler = handler;
        }
        
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request, cancellationToken));
        }
    }
}
