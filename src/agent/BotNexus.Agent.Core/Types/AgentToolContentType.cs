namespace BotNexus.Agent.Core.Types;

/// <summary>
/// Identifies the content kind carried in a tool result content block.
/// Maps to pi-mono tool output block discriminators.
/// </summary>
public enum AgentToolContentType
{
    /// <summary>
    /// Plain text content returned by the tool.
    /// </summary>
    /// <remarks>
    /// The most common content type. Value is a text string sent back to the LLM.
    /// </remarks>
    Text,

    /// <summary>
    /// Image content represented as a string payload.
    /// </summary>
    /// <remarks>
    /// Value should be a data URI (data:image/png;base64,...) or base64-encoded image.
    /// Used by multimodal models that accept images in tool results.
    /// </remarks>
    Image,
}
