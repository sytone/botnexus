namespace BotNexus.Gateway.Api.Controllers;

/// <summary>Explicit optimistic precondition; compaction never selects a session on the caller's behalf.</summary>
/// <param name="ExpectedSessionId">The current session observed by the caller. Blank values are rejected.</param>
public sealed record CompactConversationRequest(string? ExpectedSessionId);
