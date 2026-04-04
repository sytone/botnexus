namespace BotNexus.AgentCore.Types;

/// <summary>
/// Represents a single typed content item returned from a tool call.
/// Mirrors pi-mono's text/image tool content blocks.
/// </summary>
/// <param name="Type">The content type discriminator.</param>
/// <param name="Value">The serialized content value.</param>
public record AgentToolContent(AgentToolContentType Type, string Value);
