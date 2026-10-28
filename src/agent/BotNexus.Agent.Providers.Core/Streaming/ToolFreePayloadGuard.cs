using System.Text.Json.Nodes;

namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>Preserves explicit tool suppression at the provider's final payload boundary.</summary>
public static class ToolFreePayloadGuard
{
    /// <summary>
    /// Removes tool advertisement after payload customization when the caller explicitly disabled
    /// tools. Other modes, including null, leave customization unchanged. Call immediately before
    /// serialization; only top-level invocation controls are changed, never conversation history.
    /// </summary>
    /// <param name="payload">The final mutable request object, including any replacement from OnPayload.</param>
    /// <param name="mode">The caller's semantic choice; null preserves provider defaults.</param>
    /// <param name="supportsTools">False omits tool_choice rather than sending an unsupported field.</param>
    /// <param name="messagesChoice">True uses the Messages object-shaped none choice.</param>
    public static void EnforceToolFree(
        this JsonObject payload, ToolChoice? mode, bool supportsTools = true, bool messagesChoice = false)
    {
        if (mode != ToolChoice.None)
            return;

        payload.Remove("tools");
        payload.Remove("tool_stream");
        if (!supportsTools)
            payload.Remove("tool_choice");
        else if (messagesChoice)
            payload["tool_choice"] = new JsonObject { ["type"] = "none" };
        else
            payload["tool_choice"] = "none";
    }
}
