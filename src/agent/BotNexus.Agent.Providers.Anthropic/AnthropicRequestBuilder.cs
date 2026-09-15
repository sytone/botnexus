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
        var retention = options?.CacheRetention ?? CacheRetention.Short;
        var cacheControl = AnthropicMessageConverter.BuildCacheControl(retention, model.BaseUrl);

        // Anthropic rejects a request carrying more than MaxCacheBreakpoints cache_control
        // markers, so tools, system and messages draw from one shared budget instead of each
        // stamping independently. Before this was centralised the OAuth path stamped two system
        // blocks and up to three messages -- five markers, one over the limit, on any OAuth
        // conversation longer than two messages. Spend the budget in prefix order (tools, then
        // system, then the newest messages): an earlier segment is both more stable and cheaper
        // to keep cached than a later one.
        var remainingBreakpoints = cacheControl is null ? 0 : MaxCacheBreakpoints;

        List<Dictionary<string, object?>>? toolBlocks = null;
        if (context.Tools is { Count: > 0 } tools)
        {
            toolBlocks = tools.Select(t => new Dictionary<string, object?>
            {
                ["name"] = isOAuthToken ? AnthropicMessageConverter.ToClaudeCodeName(t.Name) : t.Name,
                ["description"] = t.Description,
                ["input_schema"] = AnthropicMessageConverter.NormalizeToolSchema(t.Parameters)
            }).ToList();

            // The tools array is the first segment of the cache prefix and the most stable thing
            // in the request. Its own breakpoint means a system-prompt edit re-bills the system
            // prompt alone rather than the tool schemas with it.
            if (remainingBreakpoints > 0)
            {
                toolBlocks[^1]["cache_control"] = cacheControl;
                remainingBreakpoints--;
            }
        }

        var systemBlocks = new List<Dictionary<string, object?>>();

        if (isOAuthToken)
        {
            systemBlocks.Add(new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = "You are Claude Code, Anthropic's official CLI for Claude."
            });
        }

        // Index of the last system block that is stable across requests -- the only one worth a
        // marker. Defaults to the OAuth preamble so an OAuth request with no system prompt still
        // caches it.
        var stableSystemIndex = systemBlocks.Count > 0 ? 0 : -1;

        var includeSystemPrompt = isOAuthToken
            ? !string.IsNullOrWhiteSpace(context.SystemPrompt)
            : context.SystemPrompt is not null;

        if (includeSystemPrompt)
        {
            var appended = AppendSystemPromptBlocks(systemBlocks, context.SystemPrompt!);
            if (appended >= 0)
                stableSystemIndex = appended;
        }

        // One marker on the last stable system block, never one per block: the OAuth preamble and
        // the stable prompt are adjacent and equally stable, so stamping both spent a slot for
        // nothing.
        if (stableSystemIndex >= 0 && remainingBreakpoints > 0)
        {
            systemBlocks[stableSystemIndex]["cache_control"] = cacheControl;
            remainingBreakpoints--;
        }

        var messages = AnthropicMessageConverter.ConvertMessages(context.Messages, model, isOAuthToken);
        AnthropicMessageConverter.ApplyMultiBreakpointCacheControl(
            messages,
            retention,
            model.BaseUrl,
            remainingBreakpoints);

        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = ToNode(messages),
            ["max_tokens"] = options?.MaxTokens ?? (model.MaxTokens / 3),
            ["stream"] = true
        };

        if (systemBlocks.Count > 0)
            body["system"] = ToNode(systemBlocks);

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
    /// Maximum number of <c>cache_control</c> markers the Anthropic Messages API accepts in one
    /// request. Exceeding it fails the whole request, so every marker this builder places is drawn
    /// from a single budget rather than decided independently per section.
    /// </summary>
    internal const int MaxCacheBreakpoints = CacheBreakpoints.Max;

    /// <summary>
    /// Splits the system prompt at the BOTNEXUS_CACHE_BOUNDARY marker (if present) into a stable
    /// prefix block and a dynamic tail block. When the marker is absent, the entire prompt is
    /// treated as stable. Empty segments are omitted.
    /// </summary>
    /// <returns>
    /// The index within <paramref name="blocks"/> of the last block that is stable across
    /// requests, or -1 when no stable block was appended. The caller places the marker so the
    /// <see cref="MaxCacheBreakpoints"/> budget stays in one place.
    /// </returns>
    private static int AppendSystemPromptBlocks(
        List<Dictionary<string, object?>> blocks,
        string systemPrompt)
    {
        var sanitized = systemPrompt.SanitizeSurrogates();
        var markerIndex = sanitized.IndexOf(CacheBoundaryMarker, StringComparison.Ordinal);

        if (markerIndex < 0)
        {
            // No boundary marker -- entire prompt is treated as stable.
            blocks.Add(new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = sanitized
            });
            return blocks.Count - 1;
        }

        var stableText = sanitized[..markerIndex].TrimEnd();
        var dynamicText = sanitized[(markerIndex + CacheBoundaryMarker.Length)..].TrimStart();

        // Both segments empty after trimming -- fall back to a single block.
        if (string.IsNullOrWhiteSpace(stableText) && string.IsNullOrWhiteSpace(dynamicText))
        {
            blocks.Add(new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = sanitized
            });
            return blocks.Count - 1;
        }

        var stableIndex = -1;

        if (!string.IsNullOrWhiteSpace(stableText))
        {
            blocks.Add(new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = stableText
            });
            stableIndex = blocks.Count - 1;
        }

        if (!string.IsNullOrWhiteSpace(dynamicText))
        {
            // Dynamic tail intentionally has NO cache_control.
            blocks.Add(new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = dynamicText
            });
        }

        return stableIndex;
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
