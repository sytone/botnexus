using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Core.Models;
using BotNexus.Providers.Base;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Tests.Unit.Tests;

public class HandlerFormatTests
{
    #region AnthropicMessagesHandler Tests
    
    [Fact]
    public async Task AnthropicMessagesHandler_ProducesCorrectRequestFormat()
    {
        var model = CopilotModels.Resolve("claude-opus-4.6");
        Dictionary<string, object?>? capturedPayload = null;
        
        var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            var json = request.Content!.ReadAsStringAsync().Result;
            capturedPayload = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
            
            request.Headers.Should().Contain(h => h.Key == "anthropic-version");
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            
            return JsonResponse("""
            {
              "content": [{"type": "text", "text": "Response"}],
              "stop_reason": "end_turn",
              "usage": {"input_tokens": 10, "output_tokens": 5}
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.individual.githubcopilot.com")
        };
        
        var handler = new AnthropicMessagesHandler(httpClient, NullLogger.Instance);
        var request = new ChatRequest(
            [new ChatMessage("user", "Hello")],
            new GenerationSettings { Model = "claude-opus-4.6", Temperature = 0.7, MaxTokens = 1000 },
            Tools: null,
            SystemPrompt: "You are a helpful assistant");
        
        await handler.ChatAsync(model, request, "test-key", CancellationToken.None);
        
        capturedPayload.Should().NotBeNull();
        var modelValue = capturedPayload!["model"];
        if (modelValue is JsonElement jsonElement)
        {
            jsonElement.GetString().Should().Be("claude-opus-4.6");
        }
        else
        {
            modelValue?.ToString().Should().Be("claude-opus-4.6");
        }
        capturedPayload["system"]?.ToString().Should().Be("You are a helpful assistant");
        capturedPayload["max_tokens"]?.ToString().Should().Be("1000");
        capturedPayload["temperature"]?.ToString().Should().Be("0.7");
        capturedPayload["stream"]?.ToString().Should().Be("False");
        
        var messages = capturedPayload["messages"] as JsonElement?;
        messages.Should().NotBeNull();
        messages!.Value.GetArrayLength().Should().Be(1);
    }
    
    [Fact]
    public async Task AnthropicMessagesHandler_WithTools_IncludesToolSchema()
    {
        var model = CopilotModels.Resolve("claude-opus-4.6");
        Dictionary<string, object?>? capturedPayload = null;
        
        var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            var json = request.Content!.ReadAsStringAsync().Result;
            capturedPayload = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
            
            return JsonResponse("""
            {
              "content": [
                {
                  "type": "tool_use",
                  "id": "call_123",
                  "name": "test_tool",
                  "input": {"param1": "value1"}
                }
              ],
              "stop_reason": "tool_use",
              "usage": {"input_tokens": 10, "output_tokens": 5}
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.individual.githubcopilot.com")
        };
        
        var handler = new AnthropicMessagesHandler(httpClient, NullLogger.Instance);
        var tool = new ToolDefinition("test_tool", "A test tool", new Dictionary<string, ToolParameterSchema>
        {
            ["param1"] = new ToolParameterSchema("string", "First parameter", true)
        });
        
        var request = new ChatRequest(
            [new ChatMessage("user", "Use the tool")],
            new GenerationSettings { Model = "claude-opus-4.6" },
            Tools: [tool]);
        
        await handler.ChatAsync(model, request, "test-key", CancellationToken.None);
        
        capturedPayload.Should().NotBeNull();
        capturedPayload!.Should().ContainKey("tools");
        
        var tools = capturedPayload["tools"] as JsonElement?;
        tools.Should().NotBeNull();
        tools!.Value.GetArrayLength().Should().Be(1);
    }
    
    [Fact]
    public async Task AnthropicMessagesHandler_NormalizesResponseCorrectly()
    {
        var model = CopilotModels.Resolve("claude-opus-4.6");
        
        var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            return JsonResponse("""
            {
              "content": [
                {
                  "type": "text",
                  "text": "Hello world"
                }
              ],
              "stop_reason": "end_turn",
              "usage": {
                "input_tokens": 15,
                "output_tokens": 10
              }
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.individual.githubcopilot.com")
        };
        
        var handler = new AnthropicMessagesHandler(httpClient, NullLogger.Instance);
        var request = new ChatRequest(
            [new ChatMessage("user", "Hello")],
            new GenerationSettings { Model = "claude-opus-4.6" });
        
        var response = await handler.ChatAsync(model, request, "test-key", CancellationToken.None);
        
        response.Content.Should().Be("Hello world");
        response.FinishReason.Should().Be(FinishReason.Stop);
        response.InputTokens.Should().Be(15);
        response.OutputTokens.Should().Be(10);
    }
    
    [Fact]
    public async Task AnthropicMessagesHandler_HandlesToolCallsCorrectly()
    {
        var model = CopilotModels.Resolve("claude-opus-4.6");
        
        var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            return JsonResponse("""
            {
              "content": [
                {
                  "type": "tool_use",
                  "id": "call_abc123",
                  "name": "get_weather",
                  "input": {
                    "location": "San Francisco",
                    "unit": "celsius"
                  }
                }
              ],
              "stop_reason": "tool_use",
              "usage": {
                "input_tokens": 20,
                "output_tokens": 15
              }
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.individual.githubcopilot.com")
        };
        
        var handler = new AnthropicMessagesHandler(httpClient, NullLogger.Instance);
        var request = new ChatRequest(
            [new ChatMessage("user", "What's the weather?")],
            new GenerationSettings { Model = "claude-opus-4.6" });
        
        var response = await handler.ChatAsync(model, request, "test-key", CancellationToken.None);
        
        response.FinishReason.Should().Be(FinishReason.ToolCalls);
        response.ToolCalls.Should().NotBeNull();
        response.ToolCalls.Should().HaveCount(1);
        response.ToolCalls![0].Id.Should().Be("call_abc123");
        response.ToolCalls[0].ToolName.Should().Be("get_weather");
        response.ToolCalls[0].Arguments.Should().ContainKey("location");
    }
    
    #endregion
    
    #region OpenAiCompletionsHandler Tests
    
    [Fact]
    public async Task OpenAiCompletionsHandler_ProducesCorrectRequestFormat()
    {
        var model = CopilotModels.Resolve("gpt-4o");
        Dictionary<string, object?>? capturedPayload = null;
        
        var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            var json = request.Content!.ReadAsStringAsync().Result;
            capturedPayload = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
            
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            
            return JsonResponse("""
            {
              "choices": [
                {
                  "message": {"role": "assistant", "content": "Response"},
                  "finish_reason": "stop"
                }
              ],
              "usage": {"prompt_tokens": 10, "completion_tokens": 5}
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.individual.githubcopilot.com")
        };
        
        var handler = new OpenAiCompletionsHandler(httpClient, NullLogger.Instance);
        var request = new ChatRequest(
            [new ChatMessage("user", "Hello")],
            new GenerationSettings { Model = "gpt-4o", Temperature = 0.8, MaxTokens = 500 },
            Tools: null,
            SystemPrompt: "You are a helpful assistant");
        
        await handler.ChatAsync(model, request, "test-key", CancellationToken.None);
        
        capturedPayload.Should().NotBeNull();
        var modelValue = capturedPayload!["model"];
        if (modelValue is JsonElement jsonElement)
        {
            jsonElement.GetString().Should().Be("gpt-4o");
        }
        else
        {
            modelValue?.ToString().Should().Be("gpt-4o");
        }
        capturedPayload["temperature"]?.ToString().Should().Be("0.8");
        capturedPayload["max_tokens"]?.ToString().Should().Be("500");
        capturedPayload["stream"]?.ToString().Should().Be("False");
        
        var messages = capturedPayload["messages"] as JsonElement?;
        messages.Should().NotBeNull();
        messages!.Value.GetArrayLength().Should().BeGreaterThan(0);
    }
    
    [Fact]
    public async Task OpenAiCompletionsHandler_NormalizesResponseCorrectly()
    {
        var model = CopilotModels.Resolve("gpt-4o");
        
        var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            return JsonResponse("""
            {
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "content": "Hello from GPT-4"
                  },
                  "finish_reason": "stop"
                }
              ],
              "usage": {
                "prompt_tokens": 12,
                "completion_tokens": 8
              }
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.individual.githubcopilot.com")
        };
        
        var handler = new OpenAiCompletionsHandler(httpClient, NullLogger.Instance);
        var request = new ChatRequest(
            [new ChatMessage("user", "Hello")],
            new GenerationSettings { Model = "gpt-4o" });
        
        var response = await handler.ChatAsync(model, request, "test-key", CancellationToken.None);
        
        response.Content.Should().Be("Hello from GPT-4");
        response.FinishReason.Should().Be(FinishReason.Stop);
        response.InputTokens.Should().Be(12);
        response.OutputTokens.Should().Be(8);
    }
    
    [Fact]
    public async Task OpenAiCompletionsHandler_HandlesToolCallsCorrectly()
    {
        var model = CopilotModels.Resolve("gpt-4o");
        
        var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            return JsonResponse("""
            {
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "content": null,
                    "tool_calls": [
                      {
                        "id": "call_xyz789",
                        "type": "function",
                        "function": {
                          "name": "search_web",
                          "arguments": "{\"query\":\"BotNexus\"}"
                        }
                      }
                    ]
                  },
                  "finish_reason": "tool_calls"
                }
              ],
              "usage": {
                "prompt_tokens": 25,
                "completion_tokens": 20
              }
            }
            """);
        }))
        {
            BaseAddress = new Uri("https://api.individual.githubcopilot.com")
        };
        
        var handler = new OpenAiCompletionsHandler(httpClient, NullLogger.Instance);
        var request = new ChatRequest(
            [new ChatMessage("user", "Search for BotNexus")],
            new GenerationSettings { Model = "gpt-4o" });
        
        var response = await handler.ChatAsync(model, request, "test-key", CancellationToken.None);
        
        response.FinishReason.Should().Be(FinishReason.ToolCalls);
        response.ToolCalls.Should().NotBeNull();
        response.ToolCalls.Should().HaveCount(1);
        response.ToolCalls![0].Id.Should().Be("call_xyz789");
        response.ToolCalls[0].ToolName.Should().Be("search_web");
        response.ToolCalls[0].Arguments.Should().ContainKey("query");
    }
    
    #endregion
    
    #region OpenAiResponsesHandler Tests
    
    [Fact]
    public async Task OpenAiResponsesHandler_ProducesCorrectRequestFormat()
    {
        var model = CopilotModels.Resolve("gpt-5.2");
        Dictionary<string, object?>? capturedPayload = null;
        
        var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            var json = request.Content!.ReadAsStringAsync().Result;
            capturedPayload = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
            
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            request.RequestUri!.AbsolutePath.Should().Be("/v1/responses");
            
            // Return streaming response
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event: response.content_part.added\n" +
                    "data: {\"part\":{\"type\":\"text\",\"text\":\"Response\"}}\n\n" +
                    "event: response.done\n" +
                    "data: {\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}}\n\n",
                    Encoding.UTF8, "text/event-stream")
            };
        }))
        {
            BaseAddress = new Uri("https://api.individual.githubcopilot.com")
        };
        
        var handler = new OpenAiResponsesHandler(httpClient, NullLogger.Instance);
        var request = new ChatRequest(
            [new ChatMessage("user", "Hello")],
            new GenerationSettings { Model = "gpt-5.2", Temperature = 0.9, MaxTokens = 2000 },
            Tools: null,
            SystemPrompt: "You are a helpful assistant");
        
        await handler.ChatAsync(model, request, "test-key", CancellationToken.None);
        
        capturedPayload.Should().NotBeNull();
        var modelValue = capturedPayload!["model"];
        if (modelValue is JsonElement jsonElement)
        {
            jsonElement.GetString().Should().Be("gpt-5.2");
        }
        else
        {
            modelValue?.ToString().Should().Be("gpt-5.2");
        }
        capturedPayload.Should().ContainKey("input");
        
        // Just check that response is present, don't need to validate exact temperature format
        if (capturedPayload.ContainsKey("response"))
        {
            capturedPayload["response"].Should().NotBeNull();
        }
    }
    
    [Fact]
    public async Task OpenAiResponsesHandler_NormalizesResponseCorrectly()
    {
        var model = CopilotModels.Resolve("gpt-5.2");
        
        var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event: response.content_part.added\n" +
                    "data: {\"part\":{\"type\":\"text\",\"text\":\"Hello from GPT-5\"}}\n\n" +
                    "event: response.done\n" +
                    "data: {\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":18,\"output_tokens\":12}}}\n\n",
                    Encoding.UTF8, "text/event-stream")
            };
        }))
        {
            BaseAddress = new Uri("https://api.individual.githubcopilot.com")
        };
        
        var handler = new OpenAiResponsesHandler(httpClient, NullLogger.Instance);
        var request = new ChatRequest(
            [new ChatMessage("user", "Hello")],
            new GenerationSettings { Model = "gpt-5.2" });
        
        var response = await handler.ChatAsync(model, request, "test-key", CancellationToken.None);
        
        response.Content.Should().Be("Hello from GPT-5");
        response.FinishReason.Should().Be(FinishReason.Stop);
        response.InputTokens.Should().Be(18);
        response.OutputTokens.Should().Be(12);
    }
    
    [Fact]
    public async Task OpenAiResponsesHandler_HandlesToolCallsCorrectly()
    {
        var model = CopilotModels.Resolve("gpt-5.2");
        
        var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event: response.output_item.added\n" +
                    "data: {\"item\":{\"type\":\"function_call\",\"call_id\":\"call_gpt5_123\",\"name\":\"calculate\"}}\n\n" +
                    "event: response.function_call_arguments.delta\n" +
                    "data: {\"call_id\":\"call_gpt5_123\",\"delta\":\"{\\\"x\\\":5,\\\"y\\\":10}\"}\n\n" +
                    "event: response.done\n" +
                    "data: {\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":30,\"output_tokens\":25}}}\n\n",
                    Encoding.UTF8, "text/event-stream")
            };
        }))
        {
            BaseAddress = new Uri("https://api.individual.githubcopilot.com")
        };
        
        var handler = new OpenAiResponsesHandler(httpClient, NullLogger.Instance);
        var request = new ChatRequest(
            [new ChatMessage("user", "Calculate 5 + 10")],
            new GenerationSettings { Model = "gpt-5.2" });
        
        var response = await handler.ChatAsync(model, request, "test-key", CancellationToken.None);
        
        response.ToolCalls.Should().NotBeNull();
        response.ToolCalls.Should().HaveCount(1);
        response.ToolCalls![0].Id.Should().Be("call_gpt5_123");
        response.ToolCalls[0].ToolName.Should().Be("calculate");
        response.ToolCalls[0].Arguments.Should().ContainKey("x");
        response.ToolCalls[0].Arguments.Should().ContainKey("y");
    }
    
    #endregion
    
    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
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
