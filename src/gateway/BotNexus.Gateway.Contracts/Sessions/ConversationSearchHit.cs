namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// One message that matched a content search, with the conversation it belongs to.
/// </summary>
/// <remarks>
/// Titles were searchable and what was actually said was not, so "the conversation where we fixed
/// the gateway restart" was findable only if someone had titled it that. That matters more here
/// than in most products because agents run unattended and generate transcripts nobody titles.
/// <para>
/// A hit is a MESSAGE, not a conversation, deliberately: the snippet is the evidence, and callers
/// that want one row per conversation can group. Collapsing in the store would throw away the
/// information a caller needs to show why a conversation matched.
/// </para>
/// </remarks>
/// <param name="ConversationId">The conversation the matching message belongs to, when it has one.</param>
/// <param name="SessionId">The session the message was recorded in.</param>
/// <param name="Role">Who said it - user, assistant, tool.</param>
/// <param name="Snippet">The matching text, trimmed for display.</param>
/// <param name="Timestamp">When it was said, as stored.</param>
public sealed record ConversationSearchHit(
    string? ConversationId,
    string SessionId,
    string Role,
    string Snippet,
    string? Timestamp);
