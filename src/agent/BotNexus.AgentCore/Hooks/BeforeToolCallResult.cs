namespace BotNexus.AgentCore.Hooks;

/// <summary>
/// Defines the outcome of pre-tool-call interception.
/// </summary>
/// <param name="Block">Indicates whether the tool call should be blocked.</param>
/// <param name="Reason">An optional reason for blocking.</param>
public record BeforeToolCallResult(bool Block, string? Reason = null);
