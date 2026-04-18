namespace BotNexus.Agent.Core.Hooks;

/// <summary>
/// Defines the outcome of pre-tool-call interception.
/// </summary>
/// <param name="Block">Indicates whether the tool call should be blocked (true prevents execution).</param>
/// <param name="Reason">An optional reason for blocking (used in error tool result when Block=true).</param>
/// <remarks>
/// Return from BeforeToolCallDelegate to prevent tool execution.
/// When Block=true, the tool result is marked as an error with the provided Reason.
/// </remarks>
public record BeforeToolCallResult(bool Block, string? Reason = null);
