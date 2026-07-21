using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// Converts BotNexus messages into the OpenAI-style Chat Completions <c>messages</c> array.
/// Mirrors the <c>AnthropicMessageConverter</c> pattern: this type owns the message-shaping
/// logic (system/developer role selection, user/assistant/tool-result conversion, image attachment
/// promotion, and tool-call-id normalization) previously inlined on
/// the OpenAI and GitHub Copilot completions providers (both emit the identical wire body). Unifying it
/// here (#1540, follow-up to #1377/#1408) removes the per-provider duplicate copies; the byte-identical
/// OpenAI/Copilot request bodies are guarded by <c>CopilotCompletionsProviderParityTests</c>.
/// </summary>
public static class CompletionsMessageConverter
{
    /// <summary>
    /// Builds the OpenAI Chat Completions <c>messages</c> array for the given prompt, model, and
    /// transcript. Supplied to the per-provider Completions request builder as the
    /// message-conversion delegate (the OpenAI and Copilot builders both pass this method).
    /// </summary>
    public static JsonArray Convert(
        string? systemPrompt,
        LlmModel model,
        IReadOnlyList<Message> messages,
        OpenAICompletionsCompat compat)
    {
        var result = new JsonArray();
        var transformedMessages = MessageTransformer.TransformMessages(
            messages,
            model,
            (id, sourceModel, targetProviderId) => NormalizeToolCallId(id, sourceModel, targetProviderId));
        string? lastRole = null;

        if (systemPrompt is not null)
        {
            var role = model.Reasoning && compat.SupportsDeveloperRole != false ? "developer" : "system";
            result.Add(new JsonObject { ["role"] = role, ["content"] = UnicodeSanitizer.SanitizeSurrogates(systemPrompt) });
        }

        for (var i = 0; i < transformedMessages.Count; i++)
        {
            var message = transformedMessages[i];
            if (compat.RequiresAssistantAfterToolResult == true &&
                lastRole == "toolResult" &&
                message is UserMessage)
            {
                result.Add(new JsonObject { ["role"] = "assistant", ["content"] = "I have processed the tool results." });
            }

            switch (message)
            {
                case UserMessage user:
                {
                    var userMessage = ConvertUserMessage(user, model.Input.Contains("image"));
                    if (userMessage is not null)
                        result.Add(userMessage);
                    lastRole = "user";
                    break;
                }

                case AssistantMessage assistant:
                    var assistantMessage = ConvertAssistantMessage(assistant, compat, model);
                    if (assistantMessage is not null)
                    {
                        result.Add(assistantMessage);
                        lastRole = "assistant";
                    }
                    break;

                case ToolResultMessage toolResult:
                {
                    var imageBlocks = new JsonArray();
                    var j = i;
                    for (; j < transformedMessages.Count && transformedMessages[j] is ToolResultMessage; j++)
                    {
                        var tr = (ToolResultMessage)transformedMessages[j];
                        result.Add(ConvertToolResultMessage(tr, compat, model));

                        var hasImages = tr.Content.Any(c => c is ImageContent);
                        if (hasImages && model.Input.Contains("image"))
                        {
                            foreach (var image in tr.Content.OfType<ImageContent>())
                            {
                                imageBlocks.Add(new JsonObject
                                {
                                    ["type"] = "image_url",
                                    ["image_url"] = new JsonObject
                                    {
                                        ["url"] = $"data:{image.MimeType};base64,{image.Data}"
                                    }
                                });
                            }
                        }
                    }

                    i = j - 1;
                    if (imageBlocks.Count > 0)
                    {
                        if (compat.RequiresAssistantAfterToolResult == true)
                        {
                            result.Add(new JsonObject
                            {
                                ["role"] = "assistant",
                                ["content"] = "I have processed the tool results."
                            });
                        }

                        var userContent = new JsonArray
                        {
                            new JsonObject { ["type"] = "text", ["text"] = "Attached image(s) from tool result:" }
                        };
                        foreach (var image in imageBlocks)
                            userContent.Add(image?.DeepClone());

                        result.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = userContent
                        });
                        lastRole = "user";
                    }
                    else
                    {
                        lastRole = "toolResult";
                    }

                    break;
                }
            }
        }

        return result;
    }

    private static string NormalizeToolCallId(string id, LlmModel sourceModel, string targetProviderId)
    {
        if (sourceModel.IsMistralFamily())
            return id.NormalizeMistralToolCallId();

        if (id.Contains('|'))
        {
            var callId = id.Split('|', 2)[0];
            return callId.NormalizeToolCallId(40);
        }

        if (string.Equals(targetProviderId, "openai", StringComparison.OrdinalIgnoreCase) && id.Length > 40)
            return id[..40];

        return id;
    }

    private static JsonObject? ConvertUserMessage(UserMessage user, bool supportsImages)
    {
        if (user.Content.IsText)
            return new JsonObject
            {
                ["role"] = "user",
                ["content"] = UnicodeSanitizer.SanitizeSurrogates(user.Content.Text ?? string.Empty)
            };

        var contentArray = new JsonArray();
        foreach (var block in user.Content.Blocks!)
        {
            switch (block)
            {
                case TextContent text:
                    contentArray.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = UnicodeSanitizer.SanitizeSurrogates(text.Text)
                    });
                    break;

                case ImageContent image when supportsImages:
                    contentArray.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject
                        {
                            ["url"] = $"data:{image.MimeType};base64,{image.Data}"
                        }
                    });
                    break;
            }
        }

        if (contentArray.Count == 0)
            return null;

        return new JsonObject { ["role"] = "user", ["content"] = contentArray };
    }

    private static JsonObject? ConvertAssistantMessage(AssistantMessage assistant, OpenAICompletionsCompat compat, LlmModel model)
    {
        var msg = new JsonObject { ["role"] = "assistant" };
        var textParts = new List<string>();
        var thinkingParts = new List<string>();
        var toolCalls = new JsonArray();
        var reasoningDetails = new JsonArray();

        foreach (var block in assistant.Content)
        {
            switch (block)
            {
                case TextContent text:
                    if (!string.IsNullOrWhiteSpace(text.Text))
                        textParts.Add(UnicodeSanitizer.SanitizeSurrogates(text.Text));
                    break;

                case ThinkingContent thinking:
                    if (string.IsNullOrWhiteSpace(thinking.Thinking))
                        break;

                    if (compat.RequiresThinkingAsText == true)
                    {
                        thinkingParts.Add(UnicodeSanitizer.SanitizeSurrogates(thinking.Thinking));
                    }
                    else if (!string.IsNullOrWhiteSpace(thinking.ThinkingSignature))
                    {
                        var signature = thinking.ThinkingSignature;
                        msg[signature] = string.Join("\n", assistant.Content.OfType<ThinkingContent>()
                            .Where(t => !string.IsNullOrWhiteSpace(t.Thinking))
                            .Select(t => UnicodeSanitizer.SanitizeSurrogates(t.Thinking)));
                    }
                    break;

                case ToolCallContent tc:
                    if (!string.IsNullOrWhiteSpace(tc.ThoughtSignature))
                    {
                        try
                        {
                            var parsed = JsonNode.Parse(tc.ThoughtSignature);
                            if (parsed is not null)
                                reasoningDetails.Add(parsed);
                        }
                        catch (JsonException)
                        {
                        }
                    }
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = NormalizeToolCallId(tc.Id, model, model.Provider),
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = tc.Name,
                            ["arguments"] = JsonSerializer.Serialize(tc.Arguments)
                        }
                    });
                    break;
            }
        }

        if (thinkingParts.Count > 0)
            textParts.Insert(0, string.Join("\n\n", thinkingParts));

        var textContent = textParts.Count > 0 ? string.Join("", textParts) : null;
        if (string.IsNullOrWhiteSpace(textContent) && toolCalls.Count == 0)
            return null;

        msg["content"] = string.IsNullOrWhiteSpace(textContent) ? null : JsonValue.Create(textContent);

        if (toolCalls.Count > 0)
            msg["tool_calls"] = toolCalls;
        if (reasoningDetails.Count > 0)
            msg["reasoning_details"] = reasoningDetails;

        return msg;
    }

    private static JsonObject ConvertToolResultMessage(
        ToolResultMessage toolResult, OpenAICompletionsCompat compat, LlmModel model)
    {
        var content = string.Join("\n", toolResult.Content
            .OfType<TextContent>()
            .Select(t => UnicodeSanitizer.SanitizeSurrogates(t.Text)));

        var msg = new JsonObject
        {
            ["role"] = "tool",
            ["tool_call_id"] = NormalizeToolCallId(toolResult.ToolCallId, model, model.Provider),
            ["content"] = string.IsNullOrWhiteSpace(content) ? "(see attached image)" : content
        };

        if (compat.RequiresToolResultName == true)
            msg["name"] = toolResult.ToolName;

        return msg;
    }
}

