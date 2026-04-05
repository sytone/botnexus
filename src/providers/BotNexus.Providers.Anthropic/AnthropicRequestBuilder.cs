using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Utilities;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotNexus.Providers.Anthropic;

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
        AnthropicMessageConverter.ApplyLastUserMessageCacheControl(
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
                systemBlocks.Add(new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = UnicodeSanitizer.SanitizeSurrogates(context.SystemPrompt),
                    ["cache_control"] = cacheControl
                });
            }

            body["system"] = ToNode(systemBlocks);
        }
        else if (context.SystemPrompt is { } systemPrompt)
        {
            var cacheControl = AnthropicMessageConverter.BuildCacheControl(
                options?.CacheRetention ?? CacheRetention.Short,
                model.BaseUrl);

            body["system"] = ToNode(new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = UnicodeSanitizer.SanitizeSurrogates(systemPrompt),
                    ["cache_control"] = cacheControl
                }
            });
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
            body["tool_choice"] = ToNode(toolChoice switch
            {
                "auto" => new Dictionary<string, object?> { ["type"] = "auto" },
                "any" => new Dictionary<string, object?> { ["type"] = "any" },
                "none" => new Dictionary<string, object?> { ["type"] = "none" },
                _ => new Dictionary<string, object?> { ["type"] = "tool", ["name"] = toolChoice }
            });
        }

        if (model.Reasoning && anthropicOpts?.ThinkingEnabled == true)
        {
            if (anthropicOpts.Effort is not null && isAdaptiveThinkingModel(model.Id))
            {
                body["thinking"] = ToNode(new Dictionary<string, object?> { ["type"] = "adaptive" });
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
            else if (isAdaptiveThinkingModel(model.Id))
            {
                body["thinking"] = ToNode(new Dictionary<string, object?> { ["type"] = "adaptive" });
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
            body["thinking"] = ToNode(new Dictionary<string, object?> { ["type"] = "disabled" });
            if (options?.Temperature.HasValue == true)
                body["temperature"] = options.Temperature.Value;
        }
        else if (options?.Temperature.HasValue == true)
        {
            body["temperature"] = options.Temperature.Value;
        }

        return body;
    }

    private static JsonNode? ToNode<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value);
        return JsonNode.Parse(element.GetRawText());
    }
}
