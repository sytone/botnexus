using System.Net;
using System.Text;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using BotNexus.Providers.Copilot;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Tests.Unit.Tests;

public class ProviderNormalizationTests
{
    [Fact]
    public async Task CopilotProvider_ReturnsCanonicalLlmResponse()
    {
        // Arrange: Raw Copilot API response
        var tokenStore = new InMemoryTokenStore(new OAuthToken("github-oauth-token", DateTimeOffset.UtcNow.AddMinutes(10)));
        var providerHttpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                return JsonResponse("""{"token":"copilot-token","expires_at":9999999999}""");
            }
            return JsonResponse("""
            {
              "id": "chatcmpl-123",
              "object": "chat.completion",
              "created": 1677652288,
              "model": "gpt-4o-2024-08-06",
              "choices": [
                {
                  "index": 0,
                  "message": {
                    "role": "assistant",
                    "content": "Normalized response content"
                  },
                  "finish_reason": "stop"
                }
              ],
              "usage": {
                "prompt_tokens": 25,
                "completion_tokens": 15,
                "total_tokens": 40
              }
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.githubcopilot.com")
        };

        var provider = new CopilotProvider(
            new CopilotConfig(),
            tokenStore,
            BuildNoopDeviceFlow(),
            NullLogger<CopilotProvider>.Instance,
            providerHttpClient);

        // Act
        var response = await provider.ChatAsync(new ChatRequest(
            [new ChatMessage("user", "Test")],
            new GenerationSettings { Model = "gpt-4o" }));

        // Assert: Verify LlmResponse structure
        response.Should().NotBeNull();
        response.Content.Should().Be("Normalized response content");
        response.FinishReason.Should().Be(FinishReason.Stop);
        response.InputTokens.Should().Be(25);
        response.OutputTokens.Should().Be(15);
        response.ToolCalls.Should().BeNull();
    }

    [Fact]
    public async Task FinishReasonMapping_ToolCalls_MapsCorrectly()
    {
        // Arrange: Copilot response with finish_reason "tool_calls"
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
                        "function": { "name": "test_tool", "arguments": "{}" }
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
            BaseAddress = new Uri("https://api.githubcopilot.com")
        };

        var provider = new CopilotProvider(
            new CopilotConfig(),
            tokenStore,
            BuildNoopDeviceFlow(),
            NullLogger<CopilotProvider>.Instance,
            providerHttpClient);

        // Act
        var response = await provider.ChatAsync(new ChatRequest(
            [new ChatMessage("user", "Use tool")],
            new GenerationSettings { Model = "gpt-4o" }));

        // Assert: "tool_calls" string should map to FinishReason.ToolCalls enum
        response.FinishReason.Should().Be(FinishReason.ToolCalls);
    }

    [Fact]
    public async Task FinishReasonMapping_Stop_MapsCorrectly()
    {
        // Arrange
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
                  "message": { "role": "assistant", "content": "Done" },
                  "finish_reason": "stop"
                }
              ]
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.githubcopilot.com")
        };

        var provider = new CopilotProvider(
            new CopilotConfig(),
            tokenStore,
            BuildNoopDeviceFlow(),
            NullLogger<CopilotProvider>.Instance,
            providerHttpClient);

        // Act
        var response = await provider.ChatAsync(new ChatRequest(
            [new ChatMessage("user", "Hello")],
            new GenerationSettings { Model = "gpt-4o" }));

        // Assert
        response.FinishReason.Should().Be(FinishReason.Stop);
    }

    [Fact]
    public async Task FinishReasonMapping_Length_MapsCorrectly()
    {
        // Arrange
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
                  "message": { "role": "assistant", "content": "Truncated..." },
                  "finish_reason": "length"
                }
              ]
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.githubcopilot.com")
        };

        var provider = new CopilotProvider(
            new CopilotConfig(),
            tokenStore,
            BuildNoopDeviceFlow(),
            NullLogger<CopilotProvider>.Instance,
            providerHttpClient);

        // Act
        var response = await provider.ChatAsync(new ChatRequest(
            [new ChatMessage("user", "Long response")],
            new GenerationSettings { Model = "gpt-4o" }));

        // Assert
        response.FinishReason.Should().Be(FinishReason.Length);
    }

    [Fact]
    public async Task FinishReasonMapping_ContentFilter_MapsCorrectly()
    {
        // Arrange
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
                  "message": { "role": "assistant", "content": "" },
                  "finish_reason": "content_filter"
                }
              ]
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.githubcopilot.com")
        };

        var provider = new CopilotProvider(
            new CopilotConfig(),
            tokenStore,
            BuildNoopDeviceFlow(),
            NullLogger<CopilotProvider>.Instance,
            providerHttpClient);

        // Act
        var response = await provider.ChatAsync(new ChatRequest(
            [new ChatMessage("user", "Filtered content")],
            new GenerationSettings { Model = "gpt-4o" }));

        // Assert
        response.FinishReason.Should().Be(FinishReason.ContentFilter);
    }

    [Fact]
    public async Task FinishReasonMapping_UnknownValue_MapsToOther()
    {
        // Arrange: Unknown finish_reason value
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
                  "message": { "role": "assistant", "content": "Response" },
                  "finish_reason": "unknown_future_reason"
                }
              ]
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.githubcopilot.com")
        };

        var provider = new CopilotProvider(
            new CopilotConfig(),
            tokenStore,
            BuildNoopDeviceFlow(),
            NullLogger<CopilotProvider>.Instance,
            providerHttpClient);

        // Act
        var response = await provider.ChatAsync(new ChatRequest(
            [new ChatMessage("user", "Test")],
            new GenerationSettings { Model = "gpt-4o" }));

        // Assert: Unknown values should map to FinishReason.Other
        response.FinishReason.Should().Be(FinishReason.Other);
    }

    [Fact]
    public async Task NullContent_HandledGracefully()
    {
        // Arrange: Response with missing content field
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
                    "role": "assistant"
                  },
                  "finish_reason": "stop"
                }
              ]
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.githubcopilot.com")
        };

        var provider = new CopilotProvider(
            new CopilotConfig(),
            tokenStore,
            BuildNoopDeviceFlow(),
            NullLogger<CopilotProvider>.Instance,
            providerHttpClient);

        // Act
        var response = await provider.ChatAsync(new ChatRequest(
            [new ChatMessage("user", "Test")],
            new GenerationSettings { Model = "gpt-4o" }));

        // Assert: Null content should be normalized to empty string
        response.Content.Should().Be(string.Empty);
        response.FinishReason.Should().Be(FinishReason.Stop);
    }

    [Fact]
    public async Task TokenUsage_ExtractedCorrectly()
    {
        // Arrange
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
                  "message": { "role": "assistant", "content": "Response" },
                  "finish_reason": "stop"
                }
              ],
              "usage": {
                "prompt_tokens": 100,
                "completion_tokens": 50,
                "total_tokens": 150
              }
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.githubcopilot.com")
        };

        var provider = new CopilotProvider(
            new CopilotConfig(),
            tokenStore,
            BuildNoopDeviceFlow(),
            NullLogger<CopilotProvider>.Instance,
            providerHttpClient);

        // Act
        var response = await provider.ChatAsync(new ChatRequest(
            [new ChatMessage("user", "Test")],
            new GenerationSettings { Model = "gpt-4o" }));

        // Assert: Token counts normalized from OpenAI naming to canonical names
        response.InputTokens.Should().Be(100);  // prompt_tokens → InputTokens
        response.OutputTokens.Should().Be(50); // completion_tokens → OutputTokens
    }

    [Fact]
    public async Task MissingUsageField_HandledGracefully()
    {
        // Arrange: Response without usage field
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
                  "message": { "role": "assistant", "content": "Response" },
                  "finish_reason": "stop"
                }
              ]
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.githubcopilot.com")
        };

        var provider = new CopilotProvider(
            new CopilotConfig(),
            tokenStore,
            BuildNoopDeviceFlow(),
            NullLogger<CopilotProvider>.Instance,
            providerHttpClient);

        // Act
        var response = await provider.ChatAsync(new ChatRequest(
            [new ChatMessage("user", "Test")],
            new GenerationSettings { Model = "gpt-4o" }));

        // Assert: Missing usage should result in null token counts
        response.InputTokens.Should().BeNull();
        response.OutputTokens.Should().BeNull();
    }

    [Fact]
    public async Task ToolCallParsing_PreservesToolCallId()
    {
        // Arrange: Verify tool call IDs are preserved during normalization
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
                        "id": "call_abc123",
                        "type": "function",
                        "function": { "name": "get_weather", "arguments": "{\"location\":\"NYC\"}" }
                      },
                      {
                        "id": "call_def456",
                        "type": "function",
                        "function": { "name": "get_time", "arguments": "{}" }
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
            BaseAddress = new Uri("https://api.githubcopilot.com")
        };

        var provider = new CopilotProvider(
            new CopilotConfig(),
            tokenStore,
            BuildNoopDeviceFlow(),
            NullLogger<CopilotProvider>.Instance,
            providerHttpClient);

        // Act
        var response = await provider.ChatAsync(new ChatRequest(
            [new ChatMessage("user", "Get weather and time")],
            new GenerationSettings { Model = "gpt-4o" }));

        // Assert: IDs should be preserved
        response.ToolCalls.Should().NotBeNull();
        response.ToolCalls!.Should().HaveCount(2);
        response.ToolCalls[0].Id.Should().Be("call_abc123");
        response.ToolCalls[0].ToolName.Should().Be("get_weather");
        response.ToolCalls[1].Id.Should().Be("call_def456");
        response.ToolCalls[1].ToolName.Should().Be("get_time");
    }

    [Fact]
    public async Task EmptyArgumentsString_HandledGracefully()
    {
        // Arrange: Tool call with empty arguments string
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
                        "function": { "name": "no_args_tool", "arguments": "" }
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
            BaseAddress = new Uri("https://api.githubcopilot.com")
        };

        var provider = new CopilotProvider(
            new CopilotConfig(),
            tokenStore,
            BuildNoopDeviceFlow(),
            NullLogger<CopilotProvider>.Instance,
            providerHttpClient);

        // Act
        var response = await provider.ChatAsync(new ChatRequest(
            [new ChatMessage("user", "Use tool")],
            new GenerationSettings { Model = "gpt-4o" }));

        // Assert: Empty arguments should result in empty dictionary
        response.ToolCalls.Should().NotBeNull();
        response.ToolCalls!.Should().ContainSingle();
        response.ToolCalls[0].Arguments.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static GitHubDeviceCodeFlow BuildNoopDeviceFlow()
    {
        var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("OAuth flow should not be called.")));
        return new GitHubDeviceCodeFlow(client, NullLogger<GitHubDeviceCodeFlow>.Instance);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> handler) : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestNumber = System.Threading.Interlocked.Increment(ref _requestCount);
            return Task.FromResult(handler(request, requestNumber));
        }
    }

    private sealed class InMemoryTokenStore(OAuthToken? initialToken = null) : IOAuthTokenStore
    {
        private OAuthToken? _token = initialToken;

        public Task<OAuthToken?> LoadTokenAsync(string providerName, CancellationToken cancellationToken = default)
            => Task.FromResult(_token);

        public Task SaveTokenAsync(string providerName, OAuthToken token, CancellationToken cancellationToken = default)
        {
            _token = token;
            return Task.CompletedTask;
        }

        public Task ClearTokenAsync(string providerName, CancellationToken cancellationToken = default)
        {
            _token = null;
            return Task.CompletedTask;
        }
    }
}
