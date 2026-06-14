using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.OpenAI;

/// <summary>
/// Builds the JSON request body for the OpenAI Chat Completions API. Mirrors the
/// <see cref="AnthropicRequestBuilder"/> pattern: this type owns the payload-assembly logic
/// (including the OpenRouter/Anthropic cache-control and tool-history helpers, which are used only by
/// payload assembly), while the (not-yet-extracted) message/tool conversion is supplied as delegates so
/// it can continue to live on the provider until a later refactor step relocates it to a dedicated
/// converter (#1403 step 1/6 — pure move + delegation, no behavior change).
/// </summary>
internal static class OpenAICompletionsRequestBuilder
{
    internal static JsonObject Build(
        LlmModel model,
        string? systemPrompt,
        IReadOnlyList<Message> messages,
        IReadOnlyList<Tool>? tools,
        StreamOptions? options,
        OpenAICompletionsCompat compat,
        Func<string?, LlmModel, IReadOnlyList<Message>, OpenAICompletionsCompat, JsonArray> convertMessages,
        Func<IReadOnlyList<Tool>, OpenAICompletionsCompat, JsonArray> convertTools)
    {
        var payload = new JsonObject
        {
            ["model"] = model.Id,
            ["stream"] = true,
        };

        if (options?.MaxTokens is not null)
            payload[compat.MaxTokensField] = options.MaxTokens.Value;

        if (compat.SupportsTemperature != false && options?.Temperature is not null)
            payload["temperature"] = options.Temperature.Value;

        if (compat.SupportsStore == true && compat.SupportsStoreParam != false)
            payload["store"] = false;

        if (compat.SupportsMetadata != false && options?.Metadata is { Count: > 0 })
            payload["metadata"] = JsonSerializer.SerializeToNode(options.Metadata);

        if (compat.SupportsUsageInStreaming != false)
            payload["stream_options"] = new JsonObject { ["include_usage"] = true };

        // Reasoning / thinking support
        if (options is OpenAICompletionsOptions { ReasoningEffort: not null } compOptions && model.Reasoning)
        {
            if (compat.ThinkingFormat is "openai" && compat.SupportsReasoningEffort != false)
            {
                payload["reasoning_effort"] = compOptions.ReasoningEffort;
            }
            else if (compat.ThinkingFormat is "qwen")
            {
                payload["enable_thinking"] = true;
            }
            else if (compat.ThinkingFormat is "qwen-chat-template")
            {
                payload["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = true };
            }
            else if (compat.ThinkingFormat is "openrouter")
            {
                payload["reasoning"] = new JsonObject
                {
                    ["effort"] = compOptions.ReasoningEffort
                };
            }
            else
            {
                payload["enable_thinking"] = true;
                payload["thinking_format"] = compat.ThinkingFormat;
            }
        }
        else if (compat.ThinkingFormat is "openrouter" && model.Reasoning)
        {
            payload["reasoning"] = new JsonObject
            {
                ["effort"] = "none"
            };
        }

        if (options is OpenAICompletionsOptions { ToolChoice: not null } tcOptions)
            payload["tool_choice"] = tcOptions.ToolChoice;

        payload["messages"] = convertMessages(systemPrompt, model, messages, compat);

        if (tools is { Count: > 0 } && compat.SupportsTools != false)
        {
            payload["tools"] = convertTools(tools, compat);
            if (compat.ZaiToolStream == true)
                payload["tool_stream"] = true;
        }
        else if (HasToolHistory(messages) && compat.SupportsTools != false)
        {
            payload["tools"] = new JsonArray();
        }

        if (model.BaseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase) &&
            compat.OpenRouterRouting is { } openRouterRouting &&
            (openRouterRouting.Only is { Count: > 0 } || openRouterRouting.Order is { Count: > 0 }))
        {
            payload["provider"] = JsonSerializer.SerializeToNode(openRouterRouting);
        }

        if (model.BaseUrl.Contains("ai-gateway.vercel.sh", StringComparison.OrdinalIgnoreCase) &&
            compat.VercelGatewayRouting is { } routing &&
            (routing.Only is { Count: > 0 } || routing.Order is { Count: > 0 }))
        {
            var gateway = new JsonObject();
            if (routing.Only is { Count: > 0 })
                gateway["only"] = JsonSerializer.SerializeToNode(routing.Only);
            if (routing.Order is { Count: > 0 })
                gateway["order"] = JsonSerializer.SerializeToNode(routing.Order);
            payload["providerOptions"] = new JsonObject
            {
                ["gateway"] = gateway
            };
        }

        MaybeAddOpenRouterAnthropicCacheControl(model, payload);

        return payload;
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

    private static void MaybeAddOpenRouterAnthropicCacheControl(LlmModel model, JsonObject payload)
    {
        if (!string.Equals(model.Provider, "openrouter", StringComparison.OrdinalIgnoreCase) ||
            !model.Id.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase) ||
            payload["messages"] is not JsonArray messages)
        {
            return;
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject message)
                continue;

            var role = message["role"]?.GetValue<string>();
            if (role is not ("user" or "assistant"))
                continue;

            var content = message["content"];
            if (content is JsonValue stringContent)
            {
                message["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = stringContent.ToString(),
                        ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
                    }
                };
                return;
            }

            if (content is not JsonArray contentParts)
                continue;

            for (var j = contentParts.Count - 1; j >= 0; j--)
            {
                if (contentParts[j] is JsonObject part &&
                    string.Equals(part["type"]?.GetValue<string>(), "text", StringComparison.OrdinalIgnoreCase))
                {
                    part["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
                    return;
                }
            }
        }
    }
}
