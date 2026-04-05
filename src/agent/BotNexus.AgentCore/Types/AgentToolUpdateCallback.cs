namespace BotNexus.AgentCore.Types;

/// <summary>
/// Callback used by tools to stream partial execution updates.
/// </summary>
public delegate void AgentToolUpdateCallback(AgentToolResult partialResult);
