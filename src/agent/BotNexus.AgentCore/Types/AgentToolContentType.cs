namespace BotNexus.AgentCore.Types;

/// <summary>
/// Identifies the content kind carried in a tool result content block.
/// Maps to pi-mono tool output block discriminators.
/// </summary>
public enum AgentToolContentType
{
    /// <summary>
    /// Plain text content.
    /// </summary>
    Text,

    /// <summary>
    /// Image content represented as a string payload.
    /// </summary>
    Image,
}
