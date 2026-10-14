using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>A server-derived reader's durable cursor for one conversation.</summary>
/// <param name="ConversationId">The conversation whose cursor is represented.</param>
/// <param name="Position">The opaque monotonic read position.</param>
/// <param name="Version">The store version, advanced only when the position advances.</param>
public sealed record ConversationReadStateResponse(
    ConversationId ConversationId,
    long Position,
    long Version);

/// <summary>Requests monotonic advancement of the current server-derived reader's cursor.</summary>
/// <param name="Position">The highest rendered position acknowledged by the caller.</param>
public sealed record AdvanceConversationReadStateRequest(ConversationReadPosition Position);
