using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotNexus.Agent.Providers.Copilot.Messages;

/// <summary>
/// Builds the JSON request body for the GitHub Copilot Anthropic-Messages-compatible
/// API from a <see cref="Context"/> and <see cref="StreamOptions"/>. Owned by the
/// Copilot provider; behaviour is byte-identical to the Anthropic builder for the
/// Copilot transport (no claude-cli OAuth system-prompt prefix).
/// </summary>
internal static class CopilotMessagesRequestBuilder
{
    internal static JsonObject BuildRequestBody(
        LlmModel model,
        Context context,
        StreamOptions? options,
        CopilotMessagesOptions? copilotOpts,
        Func<string, bool> isAdaptiveThinkingModel)
    {
        var retention = options?.CacheRetention ?? CacheRetention.Short;
        var cacheControl = CopilotMessagesMessageConverter.BuildCacheControl(retention, model.BaseUrl);

        // Same shared budget as the Anthropic builder this path is held byte-identical to: the
        // Messages wire format rejects a request carrying more than CacheBreakpoints.Max markers,
        // so tools, system and messages draw from one pool spent in prefix order.
        var remainingBreakpoints = cacheControl is null ? 0 : CacheBreakpoints.Max;

        List<Dictionary<string, object?>>? toolBlocks = null;
        if (context.Tools is { Count: > 0 } tools)
        {
            toolBlocks = tools.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = CopilotMessagesMessageConverter.NormalizeToolSchema(t.Parameters)
            }).ToList();

            if (remainingBreakpoints > 0)
            {
                toolBlocks[^1]["cache_control"] = cacheControl;
                remainingBreakpoints--;
            }
        }

        Dictionary<string, object?>? systemBlock = null;
        if (context.SystemPrompt is { } systemPrompt)
        {
            systemBlock = new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = systemPrompt.SanitizeSurrogates()
            };

            if (remainingBreakpoints > 0)
            {
                systemBlock["cache_control"] = cacheControl;
                remainingBreakpoints--;
            }
        }

        var messages = CopilotMessagesMessageConverter.ConvertMessages(context.Messages, model);
        if (remainingBreakpoints > 0)
        {
            CopilotMessagesMessageConverter.ApplyLastUserMessageCacheControl(
                messages,
                retention,
                model.BaseUrl);
        }

        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = ToNode(messages),
            ["max_tokens"] = options?.MaxTokens ?? (model.MaxTokens / 3),
            ["stream"] = true
        };

        if (systemBlock is not null)
            body["system"] = ToNode(new object[] { systemBlock });

        if (toolBlocks is not null)
            body["tools"] = ToNode(toolBlocks);

        if (options?.Metadata is { } metadata &&
            metadata.TryGetValue("user_id", out var rawUserId) &&
            rawUserId is string userId &&
            !string.IsNullOrWhiteSpace(userId))
        {
            body["metadata"] = ToNode(new Dictionary<string, object?>
            {
                ["user_id"] = userId
            });
        }

        if (copilotOpts?.ToolChoice is { } toolChoice)
        {
            body["tool_choice"] = BuildToolChoiceNode(toolChoice);
        }

        if (model.Reasoning && copilotOpts?.ThinkingEnabled == true)
        {
            if (isAdaptiveThinkingModel(model.Id))
            {
                body["thinking"] = ToNode(new Dictionary<string, object?> { ["type"] = "adaptive" });
                if (copilotOpts.Effort is not null)
                    body["output_config"] = ToNode(new Dictionary<string, object?> { ["effort"] = copilotOpts.Effort });
            }
            else if (copilotOpts.ThinkingBudgetTokens is { } budget)
            {
                body["thinking"] = ToNode(new Dictionary<string, object?>
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = budget
                });
            }
            else
            {
                body["thinking"] = ToNode(new Dictionary<string, object?>
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = 1024
                });
            }
        }
        else if (model.Reasoning && copilotOpts?.ThinkingEnabled == false)
        {
            // Adaptive thinking models (Opus 4.6, Sonnet 4.6) do not support
            // thinking: {type: disabled} — the API returns an empty response.
            // Instead, use adaptive mode with minimal effort so the model still
            // produces output text without expensive reasoning.
            if (isAdaptiveThinkingModel(model.Id))
            {
                body["thinking"] = ToNode(new Dictionary<string, object?> { ["type"] = "adaptive" });
                body["output_config"] = ToNode(new Dictionary<string, object?> { ["effort"] = "low" });
            }
            else
            {
                body["thinking"] = ToNode(new Dictionary<string, object?> { ["type"] = "disabled" });
            }
        }

        if (options?.Temperature.HasValue == true && copilotOpts?.ThinkingEnabled != true)
            body["temperature"] = options.Temperature.Value;

        return body;
    }

    private static JsonNode? ToNode<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value);
        return JsonNode.Parse(element.GetRawText());
    }

    private static JsonNode? BuildToolChoiceNode(object toolChoice)
    {
        if (toolChoice is JsonNode node)
        {
            return node.DeepClone();
        }

        if (toolChoice is JsonElement element)
        {
            return JsonNode.Parse(element.GetRawText());
        }

        if (toolChoice is IDictionary<string, object?> dictionary)
        {
            return ToNode(dictionary);
        }

        if (toolChoice is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            return ToNode(readOnlyDictionary);
        }

        var choice = toolChoice as string ?? toolChoice.ToString() ?? string.Empty;
        return ToNode(choice switch
        {
            "auto" => new Dictionary<string, object?> { ["type"] = "auto" },
            "any" => new Dictionary<string, object?> { ["type"] = "any" },
            "none" => new Dictionary<string, object?> { ["type"] = "none" },
            _ => new Dictionary<string, object?> { ["type"] = "tool", ["name"] = choice }
        });
    }
}
