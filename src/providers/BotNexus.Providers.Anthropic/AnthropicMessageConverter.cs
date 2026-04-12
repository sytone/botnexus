using System.Text.Json;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Utilities;

namespace BotNexus.Providers.Anthropic;

/// <summary>
/// Converts BotNexus messages to Anthropic API message format and handles
/// cache control, tool-name mapping, and tool-call ID normalization.
/// </summary>
internal static class AnthropicMessageConverter
{
    private static readonly IReadOnlyDictionary<string, string> ClaudeCodeToolLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Read"] = "Read",
        ["Write"] = "Write",
        ["Edit"] = "Edit",
        ["Bash"] = "Bash",
        ["Grep"] = "Grep",
        ["Glob"] = "Glob",
        ["AskUserQuestion"] = "AskUserQuestion",
        ["EnterPlanMode"] = "EnterPlanMode",
        ["ExitPlanMode"] = "ExitPlanMode",
        ["KillShell"] = "KillShell",
        ["NotebookEdit"] = "NotebookEdit",
        ["Skill"] = "Skill",
        ["Task"] = "Task",
        ["TaskOutput"] = "TaskOutput",
        ["TodoWrite"] = "TodoWrite",
        ["WebFetch"] = "WebFetch",
        ["WebSearch"] = "WebSearch"
    };

    internal static object NormalizeToolSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>(),
                ["required"] = Array.Empty<string>()
            };
        }

        if (schema.TryGetProperty("type", out var typeElement) &&
            string.Equals(typeElement.GetString(), "object", StringComparison.OrdinalIgnoreCase))
        {
            return schema;
        }

        var properties = schema.TryGetProperty("properties", out var propertiesElement)
            ? JsonSerializer.Deserialize<object>(propertiesElement.GetRawText())
            : new Dictionary<string, object?>();
        var required = schema.TryGetProperty("required", out var requiredElement) &&
                       requiredElement.ValueKind == JsonValueKind.Array
            ? JsonSerializer.Deserialize<object>(requiredElement.GetRawText())
            : Array.Empty<string>();

        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        };
    }

    internal static List<Dictionary<string, object?>> ConvertMessages(
        IReadOnlyList<Message> messages, LlmModel model, bool isOAuthToken)
    {
        var transformed = MessageTransformer.TransformMessages(messages, model, NormalizeToolCallId);
        var result = new List<Dictionary<string, object?>>();
        var isLastToolResult = false;

        foreach (var msg in transformed)
        {
            switch (msg)
            {
                case UserMessage user:
                    isLastToolResult = false;
                    var userMessage = ConvertUserMessage(user, model);
                    if (userMessage is not null)
                        result.Add(userMessage);
                    break;

                case AssistantMessage assistant:
                    isLastToolResult = false;
                    var assistantMessage = ConvertAssistantMessage(assistant, isOAuthToken);
                    if (assistantMessage is not null)
                        result.Add(assistantMessage);
                    break;

                case ToolResultMessage toolResult:
                    var block = MakeToolResultBlock(toolResult);
                    if (isLastToolResult && result.Count > 0 &&
                        result[^1]["content"] is List<object> existingBlocks)
                    {
                        existingBlocks.Add(block);
                    }
                    else
                    {
                        result.Add(new Dictionary<string, object?>
                        {
                            ["role"] = "user",
                            ["content"] = new List<object> { block }
                        });
                        isLastToolResult = true;
                    }
                    break;
            }
        }

        return result;
    }

    private static Dictionary<string, object?>? ConvertUserMessage(UserMessage msg, LlmModel model)
    {
        object content;

        if (msg.Content.IsText)
        {
            if (string.IsNullOrWhiteSpace(msg.Content.Text))
                return null;

            content = UnicodeSanitizer.SanitizeSurrogates(msg.Content.Text);
        }
        else
        {
            var blocks = new List<object>();
            foreach (var block in msg.Content.Blocks!)
            {
                switch (block)
                {
                    case TextContent text:
                        if (string.IsNullOrWhiteSpace(text.Text))
                            break;

                        blocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = UnicodeSanitizer.SanitizeSurrogates(text.Text)
                        });
                        break;
                    case ImageContent image:
                        if (!model.Input.Contains("image"))
                            break;
                        blocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "image",
                            ["source"] = new Dictionary<string, object?>
                            {
                                ["type"] = "base64",
                                ["media_type"] = image.MimeType,
                                ["data"] = image.Data
                            }
                        });
                        break;
                }
            }
            if (blocks.Count == 0)
                return null;

            content = blocks;
        }

        return new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = content
        };
    }

    private static Dictionary<string, object?>? ConvertAssistantMessage(AssistantMessage msg, bool isOAuthToken)
    {
        var blocks = new List<object>();

        foreach (var block in msg.Content)
        {
            switch (block)
            {
                case TextContent text:
                    if (string.IsNullOrWhiteSpace(text.Text))
                        break;

                    var textBlock = new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = UnicodeSanitizer.SanitizeSurrogates(text.Text)
                    };
                    if (text.TextSignature is not null)
                        textBlock["signature"] = text.TextSignature;
                    blocks.Add(textBlock);
                    break;

                case ThinkingContent thinking:
                    if (thinking.Redacted == true)
                    {
                        if (string.IsNullOrWhiteSpace(thinking.ThinkingSignature))
                            break;

                        blocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "redacted_thinking",
                            ["data"] = thinking.ThinkingSignature
                        });
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(thinking.Thinking))
                            break;

                        if (string.IsNullOrWhiteSpace(thinking.ThinkingSignature))
                        {
                            blocks.Add(new Dictionary<string, object?>
                            {
                                ["type"] = "text",
                                ["text"] = UnicodeSanitizer.SanitizeSurrogates(thinking.Thinking)
                            });
                            break;
                        }

                        blocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "thinking",
                            ["thinking"] = UnicodeSanitizer.SanitizeSurrogates(thinking.Thinking),
                            ["signature"] = thinking.ThinkingSignature
                        });
                    }
                    break;

                case ToolCallContent toolCall:
                    var toolUseBlock = new Dictionary<string, object?>
                    {
                        ["type"] = "tool_use",
                        ["id"] = toolCall.Id,
                        ["name"] = isOAuthToken ? ToClaudeCodeName(toolCall.Name) : toolCall.Name,
                        ["input"] = toolCall.Arguments
                    };
                    if (toolCall.ThoughtSignature is not null)
                        toolUseBlock["signature"] = toolCall.ThoughtSignature;
                    blocks.Add(toolUseBlock);
                    break;
            }
        }

        if (blocks.Count == 0)
            return null;

        return new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = blocks
        };
    }

    private static Dictionary<string, object?> MakeToolResultBlock(ToolResultMessage toolResult)
    {
        object content;
        var textBlocks = toolResult.Content.OfType<TextContent>()
            .Select(t => UnicodeSanitizer.SanitizeSurrogates(t.Text))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        var hasImages = toolResult.Content.Any(c => c is ImageContent);

        if (!hasImages && textBlocks.Count == 1)
        {
            content = textBlocks[0];
        }
        else
        {
            var blocks = new List<object>();
            if (textBlocks.Count > 0)
            {
                blocks.Add(new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = string.Join("\n", textBlocks)
                });
            }
            else if (hasImages)
            {
                blocks.Add(new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = "(see attached image)"
                });
            }

            foreach (var image in toolResult.Content.OfType<ImageContent>())
            {
                blocks.Add(new Dictionary<string, object?>
                {
                    ["type"] = "image",
                    ["source"] = new Dictionary<string, object?>
                    {
                        ["type"] = "base64",
                        ["media_type"] = image.MimeType,
                        ["data"] = image.Data
                    }
                });
            }

            content = blocks;
        }

        var result = new Dictionary<string, object?>
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = toolResult.ToolCallId,
            ["content"] = content
        };

        if (toolResult.IsError)
        {
            result["is_error"] = true;
        }

        return result;
    }

    internal static Dictionary<string, object?>? BuildCacheControl(
        CacheRetention retention, string baseUrl)
    {
        if (retention == CacheRetention.None)
            return null;

        var cacheControl = new Dictionary<string, object?> { ["type"] = "ephemeral" };

        if (retention == CacheRetention.Long &&
            baseUrl.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase))
        {
            cacheControl["ttl"] = "1h";
        }

        return cacheControl;
    }

    internal static void ApplyLastUserMessageCacheControl(
        List<Dictionary<string, object?>> messages, CacheRetention retention, string baseUrl)
    {
        if (retention == CacheRetention.None) return;

        var cacheControl = BuildCacheControl(retention, baseUrl);
        if (cacheControl is null) return;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].TryGetValue("role", out var role) && role?.ToString() == "user")
            {
                var content = messages[i]["content"];

                if (content is string textContent)
                {
                    messages[i]["content"] = new List<object>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = textContent,
                            ["cache_control"] = cacheControl
                        }
                    };
                }
                else if (content is List<object> blocks && blocks.Count > 0)
                {
                    if (blocks[^1] is Dictionary<string, object?> lastBlock)
                        lastBlock["cache_control"] = cacheControl;
                }

                break;
            }
        }
    }

    internal static string ToClaudeCodeName(string name) =>
        ClaudeCodeToolLookup.TryGetValue(name, out var canonical) ? canonical : name;

    internal static string FromClaudeCodeName(string name, IReadOnlyList<Tool>? tools)
    {
        if (tools is { Count: > 0 })
        {
            var matched = tools.FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
                return matched.Name;
        }

        return name;
    }

    internal static string NormalizeToolCallId(string id, LlmModel sourceModel, string targetProviderId)
    {
        return id.NormalizeToolCallId(64);
    }
}
