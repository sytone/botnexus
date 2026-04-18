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
    {
        var headers = new Dictionary<string, string>
        {
            ["X-Initiator"] = InferInitiator(messages),
            ["Openai-Intent"] = "conversation-edits"
        };

        if (hasImages)
        {
            headers["Copilot-Vision-Request"] = "true";
        }

        return headers;
    }
}
