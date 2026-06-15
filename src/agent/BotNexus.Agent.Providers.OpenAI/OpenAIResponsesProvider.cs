using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Diagnostics;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.OpenAI;

/// <summary>
/// OpenAI Responses API provider.
/// Port of pi-mono's openai-responses provider + shared stream processor.
/// </summary>
public sealed class OpenAIResponsesProvider(
    HttpClient httpClient,
    ILogger<OpenAIResponsesProvider> logger) : IApiProvider
{
    public string Api => "openai-responses";

    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
    {
        var stream = new LlmStream();
        var ct = options?.CancellationToken ?? CancellationToken.None;

        _ = Task.Run(async () =>
        {
            using var activity = ProviderDiagnostics.Source.StartActivity("provider.openai-responses.stream", ActivityKind.Client);
            activity?.SetTag("botnexus.provider.name", model.Provider);
            activity?.SetTag("botnexus.model", model.Id);
            activity?.SetTag("botnexus.model.api", model.Api);

            try
            {
                await StreamCoreAsync(stream, model, context, options, ct);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (OperationCanceledException)
            {
                EmitAborted(stream, model);
                activity?.SetStatus(ActivityStatusCode.Error, "Operation canceled");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "OpenAI responses stream failed for model {Model}", model.Id);
                EmitError(stream, model, ex.Message);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            }
        }, ct);

        return stream;
    }

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";
        var reasoning = ModelRegistry.SupportsExtraHigh(model) ? options?.Reasoning : SimpleOptionsHelper.ClampReasoning(options?.Reasoning);
        var responsesOptions = new OpenAIResponsesOptions
        {
            ApiKey = apiKey,
            Temperature = options?.Temperature,
            MaxTokens = options?.MaxTokens,
            CancellationToken = options?.CancellationToken ?? CancellationToken.None,
            Transport = options?.Transport ?? Transport.Sse,
            CacheRetention = options?.CacheRetention ?? CacheRetention.Short,
            SessionId = options?.SessionId,
            OnPayload = options?.OnPayload,
            Headers = options?.Headers,
            MaxRetryDelayMs = options?.MaxRetryDelayMs ?? 60000,
            Metadata = options?.Metadata
        };

        if (reasoning is not null && model.Reasoning)
            responsesOptions.ReasoningEffort = MapThinkingLevel(reasoning.Value);

        return Stream(model, context, responsesOptions);
    }

    private async Task StreamCoreAsync(
        LlmStream stream,
        LlmModel model,
        Context context,
        StreamOptions? options,
        CancellationToken ct)
    {
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"No API key for {model.Provider}. Set credentials before using model '{model.Id}'.");
        }

        var messages = MessageTransformer.TransformMessages(context.Messages, model);
        var payload = OpenAIResponsesRequestBuilder.Build(
            model, context.SystemPrompt, messages, context.Tools, options,
            ConvertMessages, ConvertTools);

        if (options?.OnPayload is not null)
        {
            var modified = await options.OnPayload(payload, model);
            if (modified is JsonObject obj)
                payload = obj;
        }

        var url = $"{model.BaseUrl.TrimEnd('/')}/responses";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (model.Headers is not null)
        {
            foreach (var (key, value) in model.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        if (string.Equals(model.Provider, "github-copilot", StringComparison.OrdinalIgnoreCase))
        {
            var hasImages = CopilotHeaders.HasVisionInput(context.Messages);
            foreach (var (key, value) in CopilotHeaders.BuildDynamicHeaders(context.Messages, hasImages))
                request.Headers.TryAddWithoutValidation(key, value);
        }

        if (options?.Headers is not null)
        {
            foreach (var (key, value) in options.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {errorBody}");
        }

        using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(responseStream, Encoding.UTF8);
        await OpenAIResponsesStreamParser.ParseAsync(stream, reader, model, options, Api, logger, EmitError, ct);
    }

    private static JsonArray ConvertMessages(IReadOnlyList<Message> messages, LlmModel model)
    {
        var result = new JsonArray();

        foreach (var message in messages)
        {
            switch (message)
            {
                case UserMessage user:
                    result.Add(ConvertUserMessage(user, model));
                    break;

                case AssistantMessage assistant:
                    foreach (var item in ConvertAssistantMessage(assistant, model))
                        result.Add(item);
                    break;

                case ToolResultMessage toolResult:
                    result.Add(ConvertToolResultMessage(toolResult, model));
                    break;
            }
        }

        return result;
    }

    private static JsonObject ConvertUserMessage(UserMessage user, LlmModel model)
    {
        if (user.Content.IsText)
        {
            return new JsonObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "input_text",
                        ["text"] = UnicodeSanitizer.SanitizeSurrogates(user.Content.Text ?? "")
                    }
                }
            };
        }

        var contentArray = new JsonArray();
        foreach (var block in user.Content.Blocks ?? [])
        {
            switch (block)
            {
                case TextContent text:
                    contentArray.Add(new JsonObject
                    {
                        ["type"] = "input_text",
                        ["text"] = UnicodeSanitizer.SanitizeSurrogates(text.Text)
                    });
                    break;

                case ImageContent image when model.Input.Contains("image"):
                    contentArray.Add(new JsonObject
                    {
                        ["type"] = "input_image",
                        ["detail"] = "auto",
                        ["image_url"] = $"data:{image.MimeType};base64,{image.Data}"
                    });
                    break;
            }
        }

        return new JsonObject
        {
            ["type"] = "message",
            ["role"] = "user",
            ["content"] = contentArray
        };
    }

    private static IReadOnlyList<JsonObject> ConvertAssistantMessage(AssistantMessage assistant, LlmModel model)
    {
        var items = new List<JsonObject>();
        var isDifferentModel = !string.Equals(assistant.ModelId, model.Id, StringComparison.Ordinal) &&
                               string.Equals(assistant.Provider, model.Provider, StringComparison.Ordinal) &&
                               string.Equals(assistant.Api, model.Api, StringComparison.Ordinal);
        var msgIndex = 0;

        foreach (var block in assistant.Content)
        {
            switch (block)
            {
                case TextContent textBlock:
                    var parsedSignature = ParseTextSignature(textBlock.TextSignature);
                    var msgId = parsedSignature?.Id;
                    if (string.IsNullOrWhiteSpace(msgId))
                    {
                        msgId = $"msg_{msgIndex}";
                    }
                    else if (msgId.Length > 64)
                    {
                        msgId = $"msg_{ShortHash(msgId)}";
                    }

                    var textItem = new JsonObject
                    {
                        ["type"] = "message",
                        ["role"] = "assistant",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "output_text",
                                ["text"] = UnicodeSanitizer.SanitizeSurrogates(textBlock.Text),
                                ["annotations"] = new JsonArray()
                            }
                        },
                        ["status"] = "completed",
                        ["id"] = msgId
                    };
                    if (parsedSignature?.Phase is not null)
                        textItem["phase"] = parsedSignature.Phase;
                    items.Add(textItem);
                    msgIndex++;
                    break;

                case ThinkingContent thinking:
                    if (!string.IsNullOrWhiteSpace(thinking.ThinkingSignature))
                    {
                        try
                        {
                            var parsed = JsonNode.Parse(thinking.ThinkingSignature);
                            if (parsed is JsonObject reasoningItem)
                                items.Add(reasoningItem);
                        }
                        catch (JsonException)
                        {
                        }
                    }
                    break;

                case ToolCallContent toolCall:
                    var (callId, itemId) = SplitToolCallId(toolCall.Id);
                    if (isDifferentModel && itemId?.StartsWith("fc_", StringComparison.Ordinal) == true)
                        itemId = null;
                    items.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = callId,
                        ["id"] = itemId,
                        ["name"] = toolCall.Name,
                        ["arguments"] = JsonSerializer.Serialize(toolCall.Arguments)
                    });
                    break;
            }
        }

        return items;
    }

    private static JsonObject ConvertToolResultMessage(ToolResultMessage toolResult, LlmModel model)
    {
        var (callId, _) = SplitToolCallId(toolResult.ToolCallId);
        var textResult = string.Join("\n", toolResult.Content.OfType<TextContent>().Select(t => t.Text));
        var hasImages = toolResult.Content.Any(c => c is ImageContent) && model.Input.Contains("image");
        JsonNode output;

        if (hasImages)
        {
            var outputParts = new JsonArray();
            if (!string.IsNullOrWhiteSpace(textResult))
            {
                outputParts.Add(new JsonObject
                {
                    ["type"] = "input_text",
                    ["text"] = UnicodeSanitizer.SanitizeSurrogates(textResult)
                });
            }

            foreach (var image in toolResult.Content.OfType<ImageContent>())
            {
                outputParts.Add(new JsonObject
                {
                    ["type"] = "input_image",
                    ["detail"] = "auto",
                    ["image_url"] = $"data:{image.MimeType};base64,{image.Data}"
                });
            }

            output = outputParts;
        }
        else
        {
            output = JsonValue.Create(UnicodeSanitizer.SanitizeSurrogates(
                string.IsNullOrWhiteSpace(textResult) ? "(see attached image)" : textResult))!;
        }

        return new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = callId,
            ["output"] = output
        };
    }

    private static JsonArray ConvertTools(IReadOnlyList<Tool> tools)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = JsonNode.Parse(tool.Parameters.GetRawText()),
                ["strict"] = false
            });
        }

        return result;
    }

    private static string MapThinkingLevel(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.ExtraHigh => "xhigh",
        _ => "medium"
    };

    private sealed record ParsedTextSignature(string Id, string? Phase);

    private static ParsedTextSignature? ParseTextSignature(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        if (signature.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(signature);
                var root = doc.RootElement;
                if (root.TryGetProperty("v", out var version) &&
                    version.ValueKind == JsonValueKind.Number &&
                    version.GetInt32() == 1 &&
                    root.TryGetProperty("id", out var idProp) &&
                    idProp.ValueKind == JsonValueKind.String)
                {
                    var id = idProp.GetString()!;
                    var phase = root.TryGetProperty("phase", out var phaseProp) &&
                                phaseProp.ValueKind == JsonValueKind.String
                        ? phaseProp.GetString()
                        : null;
                    if (phase is not ("commentary" or "final_answer"))
                        phase = null;
                    return new ParsedTextSignature(id, phase);
                }
            }
            catch (JsonException)
            {
            }
        }

        return new ParsedTextSignature(signature, null);
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();
        return null;
    }

    private static (string CallId, string? ItemId) SplitToolCallId(string id)
    {
        if (!id.Contains('|')) return (id, null);
        var parts = id.Split('|', 2);
        return (parts[0], parts[1]);
    }

    private void EmitError(
        LlmStream stream,
        LlmModel model,
        string errorMessage,
        IReadOnlyList<ContentBlock>? partialContent = null)
    {
        var message = new AssistantMessage(
            Content: partialContent?.ToList() ?? [],
            Api: Api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Error,
            ErrorMessage: errorMessage,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        stream.Push(new ErrorEvent(StopReason.Error, message));
        stream.End(message);
    }

    private void EmitAborted(LlmStream stream, LlmModel model)
    {
        var message = new AssistantMessage(
            Content: [],
            Api: Api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Aborted,
            ErrorMessage: "Request was cancelled",
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        stream.Push(new DoneEvent(StopReason.Aborted, message));
        stream.End(message);
    }
}
