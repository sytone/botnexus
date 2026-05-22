using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Diagnostics;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.GitHubModels;

/// <summary>
/// Provider for the GitHub Models inference API (https://models.inference.ai.azure.com).
/// Uses Bearer authentication with GITHUB_TOKEN. OpenAI-compatible chat completions wire format.
/// Always uses <c>system</c> role (not <c>developer</c>) per GitHub Models spec.
/// </summary>
public sealed class GitHubModelsProvider(HttpClient httpClient) : IApiProvider
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private static readonly OpenAIStreamProcessor StreamProcessor = new();

    /// <summary>GitHub Models provider key.</summary>
    public string Api => "github-models";

    private const string BaseUrl = "https://models.inference.ai.azure.com";

    /// <inheritdoc />
    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
    {
        var stream = new LlmStream();

        _ = Task.Run(async () =>
        {
            using var activity = ProviderDiagnostics.Source.StartActivity("provider.github-models.stream", ActivityKind.Client);
            activity?.SetTag("botnexus.provider.name", model.Provider);
            activity?.SetTag("botnexus.model", model.Id);
            activity?.SetTag("botnexus.model.api", model.Api);

            try
            {
                await StreamCoreAsync(model, context, options, stream);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                var errorMessage = CreateErrorMessage(model, ex.Message);
                stream.Push(new ErrorEvent(StopReason.Error, errorMessage));
                stream.End(errorMessage);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            }
        });

        return stream;
    }

    /// <inheritdoc />
    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        var apiKey = options?.ApiKey
            ?? EnvironmentApiKeys.GetApiKey(model.Provider)
            ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
            ?? "";

        var baseOptions = SimpleOptionsHelper.BuildBaseOptions(model, options, apiKey);
        return Stream(model, context, baseOptions);
    }

    private async Task StreamCoreAsync(
        LlmModel model, Context context, StreamOptions? options, LlmStream stream)
    {
        // GitHub Models: always use system role, no store, no strict mode quirks
        var compat = new OpenAICompletionsCompat
        {
            SupportsDeveloperRole = false,
            SupportsStore = false,
            SupportsStrictMode = true,
            SupportsTools = true,
            SupportsUsageInStreaming = true,
            MaxTokensField = "max_tokens",
        };

        var apiKey = options?.ApiKey
            ?? EnvironmentApiKeys.GetApiKey(model.Provider)
            ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
            ?? "";

        var ct = options?.CancellationToken ?? CancellationToken.None;

        var requestBody = BuildRequestBody(model, context, options, compat);

        if (options?.OnPayload is not null)
        {
            var modified = await options.OnPayload(requestBody, model);
            if (modified is JsonObject modifiedObject)
                requestBody = modifiedObject;
        }

        var requestJson = requestBody.ToJsonString();
        var url = $"{BaseUrl}/chat/completions";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        if (model.Headers is not null)
        {
            foreach (var (key, value) in model.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        if (options?.Headers is not null)
        {
            foreach (var (key, value) in options.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            var errorMessage = CreateErrorMessage(model, $"HTTP {(int)response.StatusCode}: {errorBody}");
            stream.Push(new ErrorEvent(StopReason.Error, errorMessage));
            stream.End(errorMessage);
            return;
        }

        using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(responseStream, Encoding.UTF8);

        await StreamProcessor.ParseCompatAsync(
            stream,
            reader,
            model,
            Api,
            ParseUsage,
            MapStopReason,
            ct);
    }

    private static JsonObject BuildRequestBody(
        LlmModel model, Context context, StreamOptions? options, OpenAICompletionsCompat compat)
    {
        var messages = BuildMessages(context, compat, model);
        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = ToNode(messages),
            ["stream"] = true,
        };

        var maxTokens = options?.MaxTokens ?? model.MaxTokens;
        body[compat.MaxTokensField] = maxTokens;

        if (options?.Temperature is not null)
            body["temperature"] = options.Temperature;

        if (compat.SupportsUsageInStreaming != false)
            body["stream_options"] = ToNode(new Dictionary<string, object?> { ["include_usage"] = true });

        if (context.Tools is { Count: > 0 } && compat.SupportsTools != false)
        {
            var tools = new List<object>();
            foreach (var tool in context.Tools)
            {
                var fn = new Dictionary<string, object?>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.Parameters,
                };

                if (compat.SupportsStrictMode != false)
                    fn["strict"] = true;

                tools.Add(new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = fn,
                });
            }
            body["tools"] = ToNode(tools);
        }
        else if (HasToolHistory(context.Messages) && compat.SupportsTools != false)
        {
            body["tools"] = ToNode(Array.Empty<object>());
        }

        return body;
    }

    private static bool HasToolHistory(IReadOnlyList<Message> messages)
    {
        foreach (var message in messages)
        {
            if (message is ToolResultMessage)
                return true;

            if (message is AssistantMessage assistant &&
                assistant.Content.Any(block => block is ToolCallContent))
            {
                return true;
            }
        }

        return false;
    }

    private static List<Dictionary<string, object?>> BuildMessages(
        Context context, OpenAICompletionsCompat compat, LlmModel model)
    {
        var messages = new List<Dictionary<string, object?>>();

        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            // GitHub Models: always system role
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = context.SystemPrompt,
            });
        }

        var supportsImages = model.Input.Contains("image");

        for (var i = 0; i < context.Messages.Count; i++)
        {
            var msg = context.Messages[i];
            switch (msg)
            {
                case UserMessage user:
                    messages.Add(BuildUserMessage(user, supportsImages));
                    break;

                case AssistantMessage assistant:
                    messages.Add(BuildAssistantMessage(assistant));
                    break;

                case ToolResultMessage toolResult:
                    messages.Add(BuildToolResultMessage(toolResult));
                    break;
            }
        }

        return messages;
    }

    private static Dictionary<string, object?> BuildUserMessage(UserMessage user, bool supportsImages)
    {
        if (user.Content.IsText)
        {
            return new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = user.Content.Text,
            };
        }

        var parts = new List<object>();
        if (user.Content.Blocks is not null)
        {
            foreach (var block in user.Content.Blocks)
            {
                switch (block)
                {
                    case TextContent text:
                        parts.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = text.Text });
                        break;

                    case ImageContent image when supportsImages:
                        parts.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new Dictionary<string, object?>
                            {
                                ["url"] = $"data:{image.MimeType};base64,{image.Data}",
                            },
                        });
                        break;
                }
            }
        }

        return new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = parts,
        };
    }

    private static Dictionary<string, object?> BuildAssistantMessage(AssistantMessage assistant)
    {
        var msg = new Dictionary<string, object?> { ["role"] = "assistant" };
        var textParts = new List<string>();
        var toolCalls = new List<object>();

        foreach (var block in assistant.Content)
        {
            switch (block)
            {
                case TextContent text:
                    textParts.Add(text.Text);
                    break;

                case ToolCallContent toolCall:
                    toolCalls.Add(new Dictionary<string, object?>
                    {
                        ["id"] = toolCall.Id,
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = toolCall.Name,
                            ["arguments"] = JsonSerializer.Serialize(toolCall.Arguments),
                        },
                    });
                    break;
            }
        }

        if (toolCalls.Count > 0)
        {
            msg["tool_calls"] = toolCalls;
            if (textParts.Count > 0)
                msg["content"] = string.Join("\n", textParts);
        }
        else
        {
            msg["content"] = string.Join("\n", textParts);
        }

        return msg;
    }

    private static Dictionary<string, object?> BuildToolResultMessage(ToolResultMessage toolResult)
    {
        var contentText = string.Join("\n", toolResult.Content
            .OfType<TextContent>()
            .Select(t => t.Text));

        return new Dictionary<string, object?>
        {
            ["role"] = "tool",
            ["tool_call_id"] = toolResult.ToolCallId,
            ["content"] = contentText,
        };
    }

    private static JsonNode? ToNode<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value);
        return JsonNode.Parse(element.GetRawText());
    }

    private static (StopReason StopReason, string? ErrorMessage) MapStopReason(string? reason, bool hasToolCalls)
        => reason switch
        {
            "stop" => (StopReason.Stop, null),
            "end" => (StopReason.Stop, null),
            "length" => (StopReason.Length, null),
            "tool_calls" => (StopReason.ToolUse, null),
            "function_call" => (StopReason.ToolUse, null),
            "content_filter" => (StopReason.Error, "Provider finish_reason: content_filter"),
            null => hasToolCalls ? (StopReason.ToolUse, null) : (StopReason.Stop, null),
            _ => (StopReason.Error, $"Provider finish_reason: {reason}")
        };

    private static Usage ParseUsage(JsonElement usageProp, Usage usage, LlmModel model)
    {
        var updated = usage;
        if (usageProp.TryGetProperty("prompt_tokens", out var inputTokens))
            updated = updated with { Input = inputTokens.GetInt32() };
        if (usageProp.TryGetProperty("completion_tokens", out var outputTokens))
            updated = updated with { Output = outputTokens.GetInt32() };
        if (usageProp.TryGetProperty("total_tokens", out var totalTokens))
            updated = updated with { TotalTokens = totalTokens.GetInt32() };

        return updated with { Cost = ModelRegistry.CalculateCost(model, updated) };
    }

    private static AssistantMessage CreateErrorMessage(LlmModel model, string error)
    {
        return new AssistantMessage(
            Content: [new TextContent(error)],
            Api: "github-models",
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Error,
            ErrorMessage: error,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );
    }
}
