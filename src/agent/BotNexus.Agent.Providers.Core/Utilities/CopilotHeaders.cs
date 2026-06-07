using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Utilities;

/// <summary>
/// GitHub Copilot dynamic headers helper.
/// Port of pi-mono's providers/github-copilot-headers.ts.
/// Shared across providers — both Anthropic and OpenAI use these when provider is github-copilot.
/// </summary>
public static class CopilotHeaders
{
    /// <summary>
    /// Infer whether the request is user-initiated or agent-initiated.
    /// Copilot expects X-Initiator header to indicate this.
    /// </summary>
    public static string InferInitiator(IReadOnlyList<Message> messages)
    {
        var last = messages.Count > 0 ? messages[^1] : null;
        return last is UserMessage ? "user" : "agent";
    }

    /// <summary>
    /// Check if any message contains image content.
    /// Copilot requires Copilot-Vision-Request header when sending images.
    /// </summary>
    public static bool HasVisionInput(IReadOnlyList<Message> messages)
    {
        foreach (var message in messages)
        {
            switch (message)
            {
                case UserMessage user when user.Content.Blocks is not null:
                    if (user.Content.Blocks.Any(b => b is ImageContent))
                        return true;
                    break;

                case ToolResultMessage toolResult:
                    if (toolResult.Content.Any(b => b is ImageContent))
                        return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>
    /// Build dynamic headers for Copilot requests.
    /// </summary>
    public static Dictionary<string, string> BuildDynamicHeaders(
        IReadOnlyList<Message> messages, bool hasImages)
        => BuildDynamicHeaders(messages, hasImages, options: null);

    /// <summary>
    /// Build dynamic headers for Copilot requests, optionally including the
    /// higher-fidelity Copilot CLI headers (Copilot-Integration-Id,
    /// X-GitHub-Api-Version, X-Interaction-Id, Editor-Version) when
    /// <paramref name="options"/> populates them. Passing <c>null</c>
    /// preserves the original three-header behaviour byte-for-byte.
    /// </summary>
    public static Dictionary<string, string> BuildDynamicHeaders(
        IReadOnlyList<Message> messages,
        bool hasImages,
        CopilotHeaderOptions? options)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Initiator"] = InferInitiator(messages),
            ["Openai-Intent"] = !string.IsNullOrEmpty(options?.IntentOverride)
                ? options!.IntentOverride!
                : "conversation-edits"
        };

        if (hasImages)
        {
            headers["Copilot-Vision-Request"] = "true";
        }

        if (options is null)
            return headers;

        if (!string.IsNullOrEmpty(options.IntegrationId))
            headers["Copilot-Integration-Id"] = options.IntegrationId!;
        if (!string.IsNullOrEmpty(options.ApiVersion))
            headers["X-GitHub-Api-Version"] = options.ApiVersion!;
        if (!string.IsNullOrEmpty(options.EditorVersion))
            headers["Editor-Version"] = options.EditorVersion!;
        if (!string.IsNullOrEmpty(options.InteractionId))
            headers["X-Interaction-Id"] = options.InteractionId!;

        return headers;
    }
}
