using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Typed routing token passed to <c>IChannelAdapter.SendStreamDeltaAsync</c> and
/// <c>IStreamEventChannelAdapter.SendStreamEventAsync</c>. Each channel adapter
/// consumes the field that matches its routing semantics:
/// </summary>
/// <remarks>
/// <para>
/// Replaces the historical <c>string conversationId</c> parameter that grew out of
/// pre-Phase 9 channel modelling. That string was an opaque per-channel token —
/// SignalR interpreted it as a <see cref="SessionId"/>, Telegram parsed it back to
/// a <see cref="ChannelAddress"/>, the internal fan-out path passed each binding's
/// channel address. The misleading parameter name and overloaded semantics were a
/// recurring source of routing bugs; this record names the four facets explicitly
/// so each adapter picks the one it needs.
/// </para>
/// <para>
/// Field consumption per built-in adapter:
/// <list type="bullet">
///   <item><b>SignalR</b> — uses <see cref="ConversationId"/> to address the SignalR
///   group. The conversation group survives compaction so stream events emitted by
///   a new session within the same conversation still reach subscribed clients.
///   Stream payloads continue to surface <see cref="SessionId"/> for client-side
///   provenance display.</item>
///   <item><b>Telegram</b> — uses <see cref="ChannelAddress"/> to recover the
///   native <c>chatId</c>/<c>messageThreadId</c> via
///   <c>TelegramChannelAddress.TryDecode</c>.</item>
///   <item><b>TUI</b> — uses <see cref="SessionId"/> only as a display label.</item>
///   <item><b>Internal</b> — uses <see cref="SessionId"/> to resolve the parent
///   session's original channel adapter and forwards the same target verbatim.</item>
///   <item><b>Observer fan-out</b> — caller constructs one target per observer
///   binding so the originating <see cref="BindingId"/> can be excluded from echo
///   and per-binding addresses are preserved.</item>
/// </list>
/// </para>
/// <para>
/// The record mirrors the shape of <see cref="OutboundMessage"/>'s addressing
/// fields so callers can lift the same values across delivery and streaming
/// paths without re-deriving anything.
/// </para>
/// </remarks>
/// <param name="ConversationId">
/// The persistent user-facing conversation the stream belongs to. Stable across
/// compaction — when a new session is created within the same conversation, this
/// id stays the same. SignalR and other user-facing channels route by this id so
/// the UI subscription survives compaction boundaries.
/// </param>
/// <param name="SessionId">
/// The session the stream events originate from. Channels that route by session
/// (internal sub-agent wake-ups) use this directly; user-facing channels surface
/// it on the payload for provenance display.
/// </param>
/// <param name="ChannelAddress">
/// The channel-native address the stream is being written to. For Portal/SignalR
/// this is typically the agent id (one binding per agent), for Telegram it is
/// <c>chatId[:threadId]</c>, for TUI it is <c>"console"</c>, and for observer
/// fan-out it is the observing binding's address rather than the originating
/// binding's address.
/// </param>
/// <param name="BindingId">
/// The originating channel binding when known. Used by fan-out paths to suppress
/// echo back to the source binding. <c>null</c> when the stream is not bound to
/// an explicit channel binding (e.g. internal sub-agent wake-ups).
/// </param>
/// <param name="ChannelRequestId">
/// Opaque identity of the originating transport request. Source adapters use it to
/// distinguish concurrent streams sharing one channel address. Observer targets leave it
/// <c>null</c> so source-specific routing state never leaks across channel fan-out.
/// </param>
public sealed record ChannelStreamTarget(
    ConversationId ConversationId,
    SessionId SessionId,
    ChannelAddress ChannelAddress,
    BindingId? BindingId = null,
    string? ChannelRequestId = null);
