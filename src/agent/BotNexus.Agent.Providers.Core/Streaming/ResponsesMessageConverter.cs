using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// Converts agent messages and tools into the OpenAI Responses API request shape (the <c>input</c>
/// array and <c>tools</c> array). These conversions were byte-identical copies on the OpenAI and
/// Copilot Responses providers; promoting them to Providers.Core (step 6/6 of #1377) lets both
/// providers collapse to thin shells while keeping the wire contract identical. The
/// <c>CopilotResponsesProviderParityTests</c> assert the two providers still emit byte-identical
/// request bodies, guarding this move.
/// </summary>
public static class ResponsesMessageConverter
{
    /// <summary>
    /// Converts the agent message list into the Responses-API <c>input</c> array, expanding assistant
    /// messages into their per-block items (text, reasoning, function calls).
    /// </summary>
    public static JsonArray ConvertMessages(IReadOnlyList<Message> messages, LlmModel model)
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

    /// <summary>
    /// Converts the agent tool catalogue into the Responses-API <c>tools</c> array.
    /// </summary>
    public static JsonArray ConvertTools(IReadOnlyList<Tool> tools)
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

    // SHA256-based short hash kept verbatim from the providers' inline helper — this id form ends up
    // in the wire payload's assistant message ids, so it must stay byte-identical for parity. (This is
    // intentionally NOT the pi-mono base-36 ShortHash.Generate used for tool-call ids.)
    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    private static (string CallId, string? ItemId) SplitToolCallId(string id)
    {
        if (!id.Contains('|')) return (id, null);
        var parts = id.Split('|', 2);
        return (parts[0], parts[1]);
    }
}
