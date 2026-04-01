namespace BotNexus.Core.Models;

/// <summary>Context passed to agent hooks during processing.</summary>
public record AgentHookContext(
    string AgentName,
    string SessionKey,
    InboundMessage? InboundMessage,
    LlmResponse? LlmResponse = null,
    Exception? Error = null);
