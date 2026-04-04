using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using BotNexus.Providers.Copilot;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Tests.Unit.Tests;

public class CopilotProviderTests
{
    [Fact]
    public async Task ChatAsync_ReturnsCompletionPayload()
    {
        var tokenStore = new InMemoryTokenStore(new OAuthToken("github-oauth-token", DateTimeOffset.UtcNow.AddMinutes(10)));
        var providerHttpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                return JsonResponse("""{"token":"copilot-token","expires_at":9999999999}""");
            }
            // gpt-4o uses OpenAI Completions API (/chat/completions)
            request.RequestUri!.AbsolutePath.Should().Be("/chat/completions");
            request.Headers.Authorization.Should().BeEquivalentTo(new AuthenticationHeaderValue("Bearer", "copilot-token"));
            return JsonResponse("""
            {
              "choices": [
                {
                  "message": { "role": "assistant", "content": "Copilot reply" },
                  "finish_reason": "stop"
                }
              ],
              "usage": { "prompt_tokens": 12, "completion_tokens": 7 }
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
            new GenerationSettings { Model = "gpt-4o" }));

        response.Content.Should().Be("Copilot reply");
        response.FinishReason.Should().Be(FinishReason.Stop);
        response.InputTokens.Should().Be(12);
        response.OutputTokens.Should().Be(7);
    }

    [Fact]
    public async Task ChatStreamAsync_ParsesSseChunks()
    {
        var tokenStore = new InMemoryTokenStore(new OAuthToken("github-oauth-token", DateTimeOffset.UtcNow.AddMinutes(10)));
        var providerHttpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                return JsonResponse("""{"token":"copilot-token","expires_at":9999999999}""");
            }
            var sse = """
            data: {"choices":[{"delta":{"content":"Hello"}}]}
            
            data: {"choices":[{"delta":{"content":" world"}}]}
            
            data: [DONE]
            
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        }))
        {
            BaseAddress = new Uri("https://api.individual.githubcopilot.com")
        };

        var provider = new CopilotProvider(new CopilotConfig(), tokenStore, BuildNoopDeviceFlow(), NullLogger<CopilotProvider>.Instance, providerHttpClient);

        var chunks = new List<string>();
        await foreach (var chunk in provider.ChatStreamAsync(new ChatRequest(
                           [new ChatMessage("user", "Stream")],
                           new GenerationSettings { Model = "gpt-4o" })))
        {
            if (!string.IsNullOrEmpty(chunk.ContentDelta))
                chunks.Add(chunk.ContentDelta);
        }

        chunks.Should().Equal("Hello", " world");
    }

    [Fact]
    public async Task ChatAsync_MapsToolCalls()
    {
        var tokenStore = new InMemoryTokenStore(new OAuthToken("github-oauth-token", DateTimeOffset.UtcNow.AddMinutes(10)));
        var providerHttpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                return JsonResponse("""{"token":"copilot-token","expires_at":9999999999}""");
            }
            return JsonResponse("""
        {
          "choices": [
            {
              "message": {
                "role": "assistant",
                "content": "",
                "tool_calls": [
                  {
                    "id": "call_1",
                    "type": "function",
                    "function": { "name": "search", "arguments": "{\"query\":\"weather\"}" }
                  }
                ]
              },
              "finish_reason": "tool_calls"
            }
          ]
        }
        """);
        }))
        {
            BaseAddress = new Uri("https://api.individual.githubcopilot.com")
        };

        var provider = new CopilotProvider(new CopilotConfig(), tokenStore, BuildNoopDeviceFlow(), NullLogger<CopilotProvider>.Instance, providerHttpClient);
        var response = await provider.ChatAsync(new ChatRequest(
            [new ChatMessage("user", "Use tool")],
            new GenerationSettings { Model = "gpt-4o" }));

        response.FinishReason.Should().Be(FinishReason.ToolCalls);
        response.ToolCalls.Should().NotBeNull();
        response.ToolCalls!.Should().ContainSingle();
        response.ToolCalls[0].ToolName.Should().Be("search");
        response.ToolCalls[0].Arguments.Keys.Should().Contain("query");
    }

    [Fact]
    public async Task DeviceCodeFlow_PollsUntilTokenReturned()
    {
        var pollCount = 0;
        var oauthHttpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/login/device/code")
            {
                return JsonResponse("""
                {
                  "device_code":"device-code",
                  "user_code":"ABCD-EFGH",
                  "verification_uri":"https://github.com/login/device",
                  "interval":1,
                  "expires_in":600
                }
                """);
            }

            pollCount++;
            return pollCount == 1
                ? JsonResponse("""{ "error":"authorization_pending" }""")
                : JsonResponse("""{ "access_token":"oauth-token","expires_in":3600 }""");
        }));

        var flow = new GitHubDeviceCodeFlow(oauthHttpClient, NullLogger<GitHubDeviceCodeFlow>.Instance);
        var token = await flow.AcquireAccessTokenAsync("Iv1.test-client");

        token.AccessToken.Should().Be("oauth-token");
        token.IsExpired.Should().BeFalse();
        pollCount.Should().Be(2);
    }

    [Fact]
    public async Task ChatAsync_CachesOAuthTokenAcrossCalls()
    {
        var oauthCalls = 0;
        var apiCalls = 0;
        var exchangeCalls = 0;
        var oauthHttpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            oauthCalls++;
            if (request.RequestUri!.AbsolutePath == "/login/device/code")
            {
                return JsonResponse("""
                {
                  "device_code":"device-code",
                  "user_code":"ABCD-EFGH",
                  "verification_uri":"https://github.com/login/device",
                  "interval":1,
                  "expires_in":600
                }
                """);
            }

            return JsonResponse("""{ "access_token":"github-oauth-token","expires_in":3600 }""");
        }));

        var providerHttpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                exchangeCalls++;
                return JsonResponse("""{"token":"copilot-token","expires_at":9999999999}""");
            }
            apiCalls++;
            request.Headers.Authorization.Should().BeEquivalentTo(new AuthenticationHeaderValue("Bearer", "copilot-token"));
            return JsonResponse("""
            {
              "choices": [
                { "message": { "role": "assistant", "content": "ok" }, "finish_reason": "stop" }
              ]
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.individual.githubcopilot.com")
        };

        var tokenStore = new InMemoryTokenStore();
        var provider = new CopilotProvider(
            new CopilotConfig(),
            tokenStore,
            new GitHubDeviceCodeFlow(oauthHttpClient, NullLogger<GitHubDeviceCodeFlow>.Instance),
            NullLogger<CopilotProvider>.Instance,
            providerHttpClient);

        var request = new ChatRequest([new ChatMessage("user", "hi")], new GenerationSettings { Model = "gpt-4o" });
        await provider.ChatAsync(request);
        await provider.ChatAsync(request);

        apiCalls.Should().Be(2);
        oauthCalls.Should().Be(2); // device code + first token exchange
        exchangeCalls.Should().Be(1); // Copilot token cached across calls
        tokenStore.SavedToken.Should().NotBeNull();
    }

    [Fact]
    public async Task ExpiredToken_TriggersReauthentication()
    {
        var expiredToken = new OAuthToken("expired", DateTimeOffset.UtcNow.AddMinutes(-5));
        var tokenStore = new InMemoryTokenStore(expiredToken);
        var oauthHttpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/login/device/code")
            {
                return JsonResponse("""
                {
                  "device_code":"device-code",
                  "user_code":"ABCD-EFGH",
                  "verification_uri":"https://github.com/login/device",
                  "interval":1,
                  "expires_in":600
                }
                """);
            }

            return JsonResponse("""{ "access_token":"fresh-github-token","expires_in":3600 }""");
        }));

        var providerHttpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                return JsonResponse("""{"token":"fresh-copilot-token","expires_at":9999999999}""");
            }
            request.Headers.Authorization.Should().BeEquivalentTo(new AuthenticationHeaderValue("Bearer", "fresh-copilot-token"));
            return JsonResponse("""
            {
              "choices": [
                { "message": { "role": "assistant", "content": "reauth" }, "finish_reason": "stop" }
              ]
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.individual.githubcopilot.com")
        };

        var provider = new CopilotProvider(
            new CopilotConfig(),
            tokenStore,
            new GitHubDeviceCodeFlow(oauthHttpClient, NullLogger<GitHubDeviceCodeFlow>.Instance),
            NullLogger<CopilotProvider>.Instance,
            providerHttpClient);

        var response = await provider.ChatAsync(new ChatRequest(
            [new ChatMessage("user", "hello")],
            new GenerationSettings { Model = "gpt-4o" }));

        response.Content.Should().Be("reauth");
        tokenStore.Cleared.Should().BeTrue();
        tokenStore.SavedToken!.AccessToken.Should().Be("fresh-github-token");
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static GitHubDeviceCodeFlow BuildNoopDeviceFlow()
    {
        var client = new HttpClient(new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("OAuth flow should not be called.")));
        return new GitHubDeviceCodeFlow(client, NullLogger<GitHubDeviceCodeFlow>.Instance);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> handler) : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref _requestCount);
            return Task.FromResult(handler(request, requestNumber));
        }
    }

    private sealed class InMemoryTokenStore(OAuthToken? initialToken = null) : IOAuthTokenStore
    {
        private OAuthToken? _token = initialToken;
        public OAuthToken? SavedToken { get; private set; }
        public bool Cleared { get; private set; }

        public Task<OAuthToken?> LoadTokenAsync(string providerName, CancellationToken cancellationToken = default)
            => Task.FromResult(_token);

        public Task SaveTokenAsync(string providerName, OAuthToken token, CancellationToken cancellationToken = default)
        {
            SavedToken = token;
            _token = token;
            return Task.CompletedTask;
        }

        public Task ClearTokenAsync(string providerName, CancellationToken cancellationToken = default)
        {
            Cleared = true;
            _token = null;
            return Task.CompletedTask;
        }
    }
}
