using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.OpenAICompat;

/// <summary>
/// Builds the human-readable text for a provider failure.
/// </summary>
/// <remarks>
/// Why this exists (#3758): the failure text the user actually reads was the bare upstream status
/// line plus response body. The provider name and the model id that was transmitted were stamped
/// onto <c>AssistantMessage.Provider</c>/<c>ModelId</c>, but those structured fields are not
/// rendered by the chat surface - so the single most common misconfiguration
/// (<c>model_not_supported</c>) reached the user with no indication of which model had been
/// rejected. Naming provider and model in the rendered text is what makes the message actionable;
/// the structured fields are unchanged and still populated.
/// </remarks>
public static class OpenAICompatErrorText
{
    /// <summary>
    /// Prefixes an error detail with the provider name and the model id that was transmitted.
    /// </summary>
    public static string Describe(LlmModel model, string? detail)
    {
        ArgumentNullException.ThrowIfNull(model);

        var provider = string.IsNullOrWhiteSpace(model.Provider) ? "(unknown)" : model.Provider;
        var modelId = string.IsNullOrWhiteSpace(model.Id) ? "(unknown)" : model.Id;
        var body = string.IsNullOrWhiteSpace(detail) ? "no further detail was reported." : detail;

        return $"Provider '{provider}' failed for model '{modelId}': {body}";
    }

    /// <summary>
    /// Renders a non-success upstream HTTP response as readable text, lifting the OpenAI-shaped
    /// <c>error.code</c> and <c>error.message</c> out of the JSON body when they are present.
    /// </summary>
    /// <remarks>
    /// Degrades rather than throws: a body that is not JSON, not an object, or is the
    /// "body exceeded N bytes" sentinel is passed through verbatim behind the status line.
    /// </remarks>
    public static string DescribeHttpFailure(int statusCode, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return $"HTTP {statusCode} (no response body).";
        }

        string? message = null;
        string? code = null;

        try
        {
            if (JsonNode.Parse(body) is JsonObject root && root["error"] is JsonObject error)
            {
                message = error["message"]?.GetValue<string>();
                code = error["code"]?.GetValue<string>();
            }
        }
        catch (JsonException)
        {
            // Not JSON - fall through to the verbatim body below.
        }
        catch (InvalidOperationException)
        {
            // JSON, but "message"/"code" were not strings. Same degradation.
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            var codePart = string.IsNullOrWhiteSpace(code) ? string.Empty : $" ({code})";
            return $"HTTP {statusCode}{codePart}. Upstream: {message}";
        }

        return $"HTTP {statusCode}: {body}";
    }
}
