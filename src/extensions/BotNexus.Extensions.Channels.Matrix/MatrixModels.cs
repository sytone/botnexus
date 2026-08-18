using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.Matrix;

// Minimal wire models for the subset of the Matrix Client-Server API this adapter consumes.
// Hand-rolled rather than taken from an SDK: the slice needs four endpoints, and the issue (#1201)
// explicitly prefers minimal dependencies. Only fields the adapter actually reads are modelled;
// unknown JSON members are ignored by System.Text.Json, so a richer homeserver payload is not an
// error.

/// <summary>Top-level response body of <c>GET /_matrix/client/v3/sync</c>.</summary>
public sealed class MatrixSyncResponse
{
    /// <summary>
    /// Opaque pagination token identifying the point this sync ended. Passed as <c>since</c> on the
    /// next request so the loop resumes exactly where it left off.
    /// </summary>
    [JsonPropertyName("next_batch")]
    public string? NextBatch { get; set; }

    /// <summary>Room state and timeline updates grouped by membership.</summary>
    [JsonPropertyName("rooms")]
    public MatrixSyncRooms? Rooms { get; set; }
}

/// <summary>Room groupings within a sync response.</summary>
public sealed class MatrixSyncRooms
{
    /// <summary>Rooms the account has joined, keyed by room ID.</summary>
    [JsonPropertyName("join")]
    public Dictionary<string, MatrixJoinedRoom>? Join { get; set; }

    /// <summary>Rooms the account has been invited to, keyed by room ID.</summary>
    [JsonPropertyName("invite")]
    public Dictionary<string, MatrixInvitedRoom>? Invite { get; set; }
}

/// <summary>A joined room's incremental update.</summary>
public sealed class MatrixJoinedRoom
{
    /// <summary>Timeline of new events in this room since the last sync.</summary>
    [JsonPropertyName("timeline")]
    public MatrixTimeline? Timeline { get; set; }
}

/// <summary>An invited room's stripped state. Presence of the entry is the invite signal.</summary>
public sealed class MatrixInvitedRoom
{
}

/// <summary>A room timeline slice.</summary>
public sealed class MatrixTimeline
{
    /// <summary>Events in the slice, oldest first.</summary>
    [JsonPropertyName("events")]
    public List<MatrixEvent>? Events { get; set; }
}

/// <summary>A single Matrix room event.</summary>
public sealed class MatrixEvent
{
    /// <summary>Event type, e.g. <c>m.room.message</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Fully-qualified Matrix user ID that sent the event.</summary>
    [JsonPropertyName("sender")]
    public string? Sender { get; set; }

    /// <summary>Globally unique event ID.</summary>
    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }

    /// <summary>Origin server timestamp in milliseconds since the Unix epoch.</summary>
    [JsonPropertyName("origin_server_ts")]
    public long? OriginServerTs { get; set; }

    /// <summary>Event content.</summary>
    [JsonPropertyName("content")]
    public MatrixMessageContent? Content { get; set; }
}

/// <summary>Content of an <c>m.room.message</c> event.</summary>
public sealed class MatrixMessageContent
{
    /// <summary>Message type, e.g. <c>m.text</c>, <c>m.notice</c>, <c>m.image</c>.</summary>
    [JsonPropertyName("msgtype")]
    public string? MsgType { get; set; }

    /// <summary>Plain-text message body.</summary>
    [JsonPropertyName("body")]
    public string? Body { get; set; }

    /// <summary>Format identifier for <see cref="FormattedBody"/>, e.g. <c>org.matrix.custom.html</c>.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    /// <summary>Formatted (HTML) message body when <see cref="Format"/> is set.</summary>
    [JsonPropertyName("formatted_body")]
    public string? FormattedBody { get; set; }

    /// <summary>Event relationship, used for <c>m.replace</c> edits and <c>m.thread</c> replies.</summary>
    [JsonPropertyName("m.relates_to")]
    public MatrixRelatesTo? RelatesTo { get; set; }

    /// <summary>
    /// Replacement content for an <c>m.replace</c> edit. Matrix carries the new body here while
    /// the top-level body remains the fallback shown by clients that do not render edits.
    /// </summary>
    [JsonPropertyName("m.new_content")]
    public MatrixMessageContent? NewContent { get; set; }
}

/// <summary>An event relationship.</summary>
public sealed class MatrixRelatesTo
{
    /// <summary>Relation type, e.g. <c>m.replace</c> or <c>m.thread</c>.</summary>
    [JsonPropertyName("rel_type")]
    public string? RelType { get; set; }

    /// <summary>The event this relation targets.</summary>
    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }
}

/// <summary>Response body of the send-event and redact endpoints.</summary>
public sealed class MatrixSendResponse
{
    /// <summary>ID of the event the homeserver created.</summary>
    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }
}
