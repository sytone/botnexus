namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Request payload for <c>POST /api/commands/execute</c> (#2873). Mirrors the gateway-side
/// <c>CommandExecuteRequest</c> record so the portal can reach the command pipeline directly
/// instead of routing command text through the agent as an ordinary user message.
/// </summary>
/// <param name="Input">Raw command input including the leading slash, e.g. <c>/status</c>.</param>
/// <param name="AgentId">Agent the command executes against, when one is selected.</param>
/// <param name="SessionId">
/// Active session for the conversation. Session-scoped commands (<c>/context</c>, <c>/model</c>,
/// <c>/reasoning</c>) need this to resolve the live handle; the gateway degrades gracefully when
/// it is absent rather than failing, so a null value is a valid request.
/// </param>
public sealed record CommandExecuteRequestDto(string Input, string? AgentId, string? SessionId);

/// <summary>
/// Result of a gateway command execution (#2873). Mirrors the gateway-side <c>CommandResult</c>
/// contract returned by <c>POST /api/commands/execute</c>.
/// </summary>
/// <param name="Title">Short result heading, e.g. <c>Gateway Status</c>.</param>
/// <param name="Body">Result body text, rendered as markdown in the chat transcript.</param>
/// <param name="IsError">
/// True when the pipeline rejected or failed the command. The portal renders these as an error
/// row so a rejection is visible rather than silently swallowed.
/// </param>
public sealed record CommandResultDto(string Title, string Body, bool IsError);
