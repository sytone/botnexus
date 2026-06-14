using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.OpenAI;

/// <summary>
/// Builds the JSON request body for the OpenAI Responses API from a model, system prompt, message
/// list, tools, and stream options. Mirrors the <see cref="AnthropicRequestBuilder"/> pattern: this
/// type owns the payload-assembly logic, while the (not-yet-extracted) message/tool conversion is
/// supplied as delegates so it can continue to live on the provider until a later refactor step
/// relocates it to a dedicated converter (#1403 step 1/6 — pure move + delegation, no behavior change).
/// </summary>
internal static class OpenAIResponsesRequestBuilder
{
    internal static JsonObject Build(
        LlmModel model,
        string? systemPrompt,
        IReadOnlyList<Message> messages,
        IReadOnlyList<Tool>? tools,
        StreamOptions? options,
        Func<IReadOnlyList<Message>, LlmModel, JsonArray> convertMessages,
        Func<IReadOnlyList<Tool>, JsonArray> convertTools)
    {
        var input = convertMessages(messages, model);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            input.Insert(0, new JsonObject
            {
                ["role"] = model.Reasoning ? "developer" : "system",
                ["content"] = UnicodeSanitizer.SanitizeSurrogates(systemPrompt)
            });
        }

        var payload = new JsonObject
        {
            ["model"] = model.Id,
            ["stream"] = true,
            ["store"] = false,
            ["input"] = input
        };

        if (options?.MaxTokens is not null)
            payload["max_output_tokens"] = options.MaxTokens.Value;

        if (options?.Temperature is not null)
            payload["temperature"] = options.Temperature.Value;

        if (options is OpenAIResponsesOptions { ServiceTier: not null } responsesOptions)
            payload["service_tier"] = responsesOptions.ServiceTier;

        if (options?.CacheRetention != CacheRetention.None && !string.IsNullOrWhiteSpace(options?.SessionId))
            payload["prompt_cache_key"] = options.SessionId;
        var promptCacheRetention = GetPromptCacheRetention(model.BaseUrl, options?.CacheRetention ?? CacheRetention.Short);
        if (!string.IsNullOrWhiteSpace(promptCacheRetention))
            payload["prompt_cache_retention"] = promptCacheRetention;

        if (tools is { Count: > 0 })
            payload["tools"] = convertTools(tools);

        if (model.Reasoning)
        {
            var effort = options is OpenAIResponsesOptions { ReasoningEffort: not null } ro ? ro.ReasoningEffort : null;
            var summary = options is OpenAIResponsesOptions { ReasoningSummary: not null } rs ? rs.ReasoningSummary : null;
            if (effort is not null || summary is not null)
            {
                payload["reasoning"] = new JsonObject
                {
                    ["effort"] = effort ?? "medium",
                    ["summary"] = summary ?? "auto"
                };
                payload["include"] = new JsonArray { "reasoning.encrypted_content" };
            }
            else if (!string.Equals(model.Provider, "github-copilot", StringComparison.OrdinalIgnoreCase))
            {
                payload["reasoning"] = new JsonObject
                {
                    ["effort"] = "none"
                };
            }
        }

        return payload;
    }

    private static string? GetPromptCacheRetention(string baseUrl, CacheRetention retention)
    {
        if (retention != CacheRetention.Long)
            return null;

        return baseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase) ? "24h" : null;
    }
}
