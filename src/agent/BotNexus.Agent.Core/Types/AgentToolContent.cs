namespace BotNexus.Agent.Core.Types;

/// <summary>
/// Represents a single typed content item returned from a tool call.
/// Mirrors pi-mono's text/image tool content blocks.
/// </summary>
/// <param name="Type">The content type discriminator (Text or Image).</param>
/// <param name="Value">The serialized content value (text string or image data URI/base64).</param>
public record AgentToolContent(AgentToolContentType Type, string Value);
