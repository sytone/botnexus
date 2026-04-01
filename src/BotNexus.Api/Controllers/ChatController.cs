using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Api.Controllers;

/// <summary>OpenAI-compatible chat completions endpoint.</summary>
[ApiController]
[Route("v1")]
public sealed class ChatController : ControllerBase
{
    private readonly ILlmProvider? _provider;

    public ChatController(ILlmProvider? provider = null)
    {
        _provider = provider;
    }

    /// <summary>OpenAI-compatible chat completions.</summary>
    [HttpPost("chat/completions")]
    public async Task<IActionResult> ChatCompletions(
        [FromBody] ChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        if (_provider is null)
            return StatusCode(503, new { error = "No LLM provider configured" });

        var messages = request.Messages.Select(m => new ChatMessage(m.Role, m.Content)).ToList();
        var settings = new GenerationSettings
        {
            Model = request.Model ?? _provider.DefaultModel,
            MaxTokens = request.MaxTokens ?? 8192,
            Temperature = request.Temperature ?? 0.1
        };

        var chatRequest = new ChatRequest(messages, settings);

        if (request.Stream == true)
        {
            Response.Headers.ContentType = "text/event-stream";
            await foreach (var delta in _provider.ChatStreamAsync(chatRequest, cancellationToken))
            {
                var chunk = new
                {
                    id = $"chatcmpl-{Guid.NewGuid():N}",
                    @object = "chat.completion.chunk",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = settings.Model,
                    choices = new[] { new { delta = new { content = delta }, index = 0, finish_reason = (string?)null } }
                };
                await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(chunk)}\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
            await Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
            return new EmptyResult();
        }

        var response = await _provider.ChatAsync(chatRequest, cancellationToken);
        return Ok(new
        {
            id = $"chatcmpl-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = settings.Model,
            choices = new[]
            {
                new
                {
                    message = new { role = "assistant", content = response.Content },
                    index = 0,
                    finish_reason = response.FinishReason.ToString().ToLowerInvariant()
                }
            },
            usage = new
            {
                prompt_tokens = response.InputTokens ?? 0,
                completion_tokens = response.OutputTokens ?? 0,
                total_tokens = (response.InputTokens ?? 0) + (response.OutputTokens ?? 0)
            }
        });
    }
}

/// <summary>OpenAI-compatible chat completion request.</summary>
public sealed record ChatCompletionRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("model")] string? Model,
    [property: System.Text.Json.Serialization.JsonPropertyName("messages")] List<ChatMessageDto> Messages,
    [property: System.Text.Json.Serialization.JsonPropertyName("max_tokens")] int? MaxTokens = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("temperature")] double? Temperature = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("stream")] bool? Stream = null);

/// <summary>DTO for a single chat message in a request.</summary>
public sealed record ChatMessageDto(
    [property: System.Text.Json.Serialization.JsonPropertyName("role")] string Role,
    [property: System.Text.Json.Serialization.JsonPropertyName("content")] string Content);
