using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotNexus.Agent.Providers.Anthropic;

/// <summary>
/// Builds the JSON request body for the Anthropic Messages API from a
/// <see cref="Context"/> and <see cref="StreamOptions"/>.
/// </summary>
internal static class AnthropicRequestBuilder
{
    internal static JsonObject BuildRequestBody(
        LlmModel model,
        Context context,
        StreamOptions? options,
        AnthropicOptions? anthropicOpts,
        bool isOAuthToken,
        Func<string, bool> isAdaptiveThinkingModel)
    {
        var messages = AnthropicMessageConverter.ConvertMessages(context.Messages, model, isOAuthToken);
        AnthropicMessageConverter.ApplyMultiBreakpointCacheControl(
            messages,
            options?.CacheRetention ?? CacheRetention.Short,
            model.BaseUrl);

        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = ToNode(messages),
            ["max_tokens"] = options?.MaxTokens ?? (model.MaxTokens / 3),
            ["stream"] = true
        };

        if (isOAuthToken)
        {
            var cacheControl = AnthropicMessageConverter.BuildCacheControl(
                options?.CacheRetention ?? CacheRetention.Short,
                model.BaseUrl);

            var systemBlocks = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["type"] = "text",
                    ["text"] = "You are Claude Code, Anthropic's official CLI for Claude.",
                    ["cache_control"] = cacheControl
                }
            };

            if (!string.IsNullOrWhiteSpace(context.SystemPrompt))
            {
                AppendSystemPromptBlocks(systemBlocks, context.SystemPrompt, cacheControl);
            }

            body["system"] = ToNode(systemBlocks);
        }
        else if (context.SystemPrompt is { } systemPrompt)
        {
            var cacheControl = AnthropicMessageConverter.BuildCacheControl(
                options?.CacheRetention ?? CacheRetention.Short,
                model.BaseUrl);

            var systemBlocks = new List<Dictionary<string, object?>>();
            AppendSystemPromptBlocks(systemBlocks, systemPrompt, cacheControl);
            body["system"] = ToNode(systemBlocks);
        }

        if (context.Tools is { Count: > 0 } tools)
        {
            body["tools"] = ToNode(tools.Select(t => new Dictionary<string, object?>
            {
                ["name"] = isOAuthToken ? AnthropicMessageConverter.ToClaudeCodeName(t.Name) : t.Name,
                ["description"] = t.Description,
                ["input_schema"] = AnthropicMessageConverter.NormalizeToolSchema(t.Parameters)
            }).ToList());
        }

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

        if (anthropicOpts?.ToolChoice is { } toolChoice)
        {
            body["tool_choice"] = BuildToolChoiceNode(toolChoice);
        }

        if (model.Reasoning && anthropicOpts?.ThinkingEnabled == true)
        {
            if (isAdaptiveThinkingModel(model.Id))
            {
                body["thinking"] = ToNode(new Dictionary<string, object?> { ["type"] = "adaptive" });
                if (anthropicOpts.Effort is not null)
                    body["output_config"] = ToNode(new Dictionary<string, object?> { ["effort"] = anthropicOpts.Effort });
            }
            else if (anthropicOpts.ThinkingBudgetTokens is { } budget)
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
        else if (model.Reasoning && anthropicOpts?.ThinkingEnabled == false)
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

        if (options?.Temperature.HasValue == true && anthropicOpts?.ThinkingEnabled != true)
            body["temperature"] = options.Temperature.Value;

        return body;
    }

    private const string CacheBoundaryMarker = "\n<!-- BOTNEXUS_CACHE_BOUNDARY -->\n";

    /// <summary>
    /// Splits the system prompt at the BOTNEXUS_CACHE_BOUNDARY marker (if present) into
    /// a stable prefix block (with cache_control) and a dynamic tail block (without).
    /// When the marker is absent, the entire prompt is treated as stable.
    /// Empty segments are omitted.
    /// </summary>
    private static void AppendSystemPromptBlocks(
        List<Dictionary<string, object?>> blocks,
        string systemPrompt,
        Dictionary<string, object?>? cacheControl)
    {
        var sanitized = UnicodeSanitizer.SanitizeSurrogates(systemPrompt);
        var markerIndex = sanitized.IndexOf(CacheBoundaryMarker, StringComparison.Ordinal);

        if (markerIndex < 0)
        {
            // No boundary marker -- entire prompt is stable (gets cache_control)
            var block = new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = sanitized
            };
            if (cacheControl is not null)
                block["cache_control"] = cacheControl;
            blocks.Add(block);
            return;
        }

        var stableText = sanitized[..markerIndex].TrimEnd();
        var dynamicText = sanitized[(markerIndex + CacheBoundaryMarker.Length)..].TrimStart();

        if (!string.IsNullOrWhiteSpace(stableText))
        {
            var stableBlock = new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = stableText
            };
            if (cacheControl is not null)
                stableBlock["cache_control"] = cacheControl;
            blocks.Add(stableBlock);
        }

        if (!string.IsNullOrWhiteSpace(dynamicText))
        {
            // Dynamic tail intentionally has NO cache_control
            blocks.Add(new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = dynamicText
            });
        }

        // If both segments are empty after trimming, fall back to single block
        if (string.IsNullOrWhiteSpace(stableText) && string.IsNullOrWhiteSpace(dynamicText))
        {
            blocks.Add(new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = sanitized
            });
            if (cacheControl is not null)
                blocks[^1]["cache_control"] = cacheControl;
        }
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
