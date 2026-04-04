using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using BotNexus.Providers.Copilot;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Tests.Unit.Tests;

public class MultiChoiceResponseParsingTests
{
    [Fact]
    public async Task SingleChoiceWithContentAndToolCalls_ParsesCorrectly()
    {
        // Arrange: Response with single choice containing both content and tool_calls
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
                    "content": "I'll use the search tool",
                    "tool_calls": [
                      {
                        "id": "call_1",
                        "type": "function",
                        "function": { "name": "search", "arguments": "{\"query\":\"test\"}" }
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
            [new ChatMessage("user", "Search for test")],
            new GenerationSettings { Model = "gpt-4o" }));

        // Assert
        response.Content.Should().Be("I'll use the search tool");
        response.FinishReason.Should().Be(FinishReason.ToolCalls);
        response.ToolCalls.Should().NotBeNull();
        response.ToolCalls!.Should().ContainSingle();
        response.ToolCalls[0].ToolName.Should().Be("search");
    }

    [Fact]
    public async Task TwoChoices_ContentInFirstToolCallsInSecond_MergesCorrectly()
    {
        // Arrange: Claude via Copilot splits response across choices
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
                    "content": "Let me help you with that"
                  },
                  "finish_reason": "stop"
                },
                {
                  "message": {
                    "role": "assistant",
                    "content": "",
                    "tool_calls": [
                      {
                        "id": "call_1",
                        "type": "function",
                        "function": { "name": "helper", "arguments": "{\"action\":\"assist\"}" }
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
            [new ChatMessage("user", "Help me")],
            new GenerationSettings { Model = "claude-3.5-sonnet" }));

        // Assert: Should take content from first choice, tool_calls from second
        response.Content.Should().Be("Let me help you with that");
        response.ToolCalls.Should().NotBeNull();
        response.ToolCalls!.Should().ContainSingle();
        response.ToolCalls[0].ToolName.Should().Be("helper");
        response.FinishReason.Should().Be(FinishReason.Stop); // First finish_reason wins
    }

    [Fact]
    public async Task TwoChoices_ToolCallsInFirstContentInSecond_MergesCorrectly()
    {
        // Arrange: Reverse order - tool_calls in first choice, content in second
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
                        "function": { "name": "fetch_data", "arguments": "{\"source\":\"api\"}" }
                      }
                    ]
                  },
                  "finish_reason": "tool_calls"
                },
                {
                  "message": {
                    "role": "assistant",
                    "content": "Fetching data from API"
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
            [new ChatMessage("user", "Get data")],
            new GenerationSettings { Model = "claude-3.5-sonnet" }));

        // Assert: Empty content in first choice, so take from second
        response.Content.Should().Be("Fetching data from API");
        response.ToolCalls.Should().NotBeNull();
        response.ToolCalls!.Should().ContainSingle();
        response.ToolCalls[0].ToolName.Should().Be("fetch_data");
        response.FinishReason.Should().Be(FinishReason.ToolCalls); // First finish_reason
    }

    [Fact]
    public async Task EmptyToolCallsArray_NoToolCallsReturned()
    {
        // Arrange: Response with empty tool_calls array
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
                    "content": "Just a text response",
                    "tool_calls": []
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
            [new ChatMessage("user", "Hello")],
            new GenerationSettings { Model = "gpt-4o" }));

        // Assert: Empty tool_calls array should result in null ToolCalls
        response.Content.Should().Be("Just a text response");
        response.FinishReason.Should().Be(FinishReason.Stop);
        response.ToolCalls.Should().BeNull();
    }

    [Fact]
    public async Task ArgumentsAsJsonString_ParsedCorrectly()
    {
        // Arrange: OpenAI format - arguments as JSON string
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
                        "function": { 
                          "name": "calculate", 
                          "arguments": "{\"operation\":\"add\",\"x\":5,\"y\":10}"
                        }
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
            [new ChatMessage("user", "Calculate 5 + 10")],
            new GenerationSettings { Model = "gpt-4o" }));

        // Assert
        response.ToolCalls.Should().NotBeNull();
        response.ToolCalls!.Should().ContainSingle();
        var toolCall = response.ToolCalls[0];
        toolCall.ToolName.Should().Be("calculate");
        toolCall.Arguments.Should().ContainKeys("operation", "x", "y");
        // JSON deserialization returns JsonElement - verify keys are present
        toolCall.Arguments.Count.Should().Be(3);
    }

    [Fact]
    public async Task ArgumentsAsJsonObject_ParsedCorrectly()
    {
        // Arrange: Claude format - arguments as JSON object
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
                        "function": { 
                          "name": "search", 
                          "arguments": {
                            "query": "weather",
                            "location": "Seattle",
                            "units": "metric"
                          }
                        }
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
            [new ChatMessage("user", "Weather in Seattle")],
            new GenerationSettings { Model = "claude-3.5-sonnet" }));

        // Assert
        response.ToolCalls.Should().NotBeNull();
        response.ToolCalls!.Should().ContainSingle();
        var toolCall = response.ToolCalls[0];
        toolCall.ToolName.Should().Be("search");
        toolCall.Arguments.Should().ContainKeys("query", "location", "units");
        // JSON deserialization returns JsonElement - verify all keys are present
        toolCall.Arguments.Count.Should().Be(3);
    }

    [Fact]
    public async Task MultipleChoicesWithEmptyContent_TakesFirstNonEmpty()
    {
        // Arrange: Multiple choices, first has empty content, second has content
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
                    "content": ""
                  },
                  "finish_reason": "stop"
                },
                {
                  "message": {
                    "role": "assistant",
                    "content": "This is the actual response"
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
            [new ChatMessage("user", "Hello")],
            new GenerationSettings { Model = "claude-3.5-sonnet" }));

        // Assert: Should skip empty content and take first non-empty
        response.Content.Should().Be("This is the actual response");
    }

    [Fact]
    public async Task MultipleChoicesWithNullContent_HandlesGracefully()
    {
        // Arrange: Choices with null content property
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
                },
                {
                  "message": {
                    "role": "assistant",
                    "content": "Valid content"
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

        // Assert: Should handle missing content and take first valid content
        response.Content.Should().Be("Valid content");
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
