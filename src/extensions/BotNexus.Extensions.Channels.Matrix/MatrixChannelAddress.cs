using BotNexus.Domain.Primitives;

namespace BotNexus.Extensions.Channels.Matrix;

/// <summary>
/// Matrix-specific encoder/decoder folding the native room ID and optional thread root event ID
/// into the opaque <see cref="ChannelAddress"/> the core uses for routing.
/// </summary>
/// <remarks>
/// <para>
/// The chosen format is <c>&lt;roomId&gt;</c> or <c>&lt;roomId&gt;/thread:&lt;eventId&gt;</c>.
/// Examples:
/// </para>
/// <list type="bullet">
///   <item><description>room root: <c>!abc123:example.com</c></description></item>
///   <item><description>thread: <c>!abc123:example.com/thread:$evt456</c></description></item>
/// </list>
/// <para>
/// A Matrix room ID has the grammar <c>!opaque_id:server_name</c>. Neither the opaque localpart
/// nor the server name may contain a forward slash, so <c>/</c> is a safe delimiter for this
/// channel. The <c>thread:</c> prefix keeps the encoding self-describing in logs and SQL, matching
/// the convention <see cref="TelegramChannelAddress"/>-style encoders established platform-wide.
/// </para>
/// <para>
/// <see cref="ChannelAddress"/> stays opaque to the core router; this encoding is a Matrix
/// convention only.
/// </para>
/// </remarks>
public static class MatrixChannelAddress
{
    private const string ThreadSeparator = "/thread:";

    /// <summary>
    /// Encodes a Matrix room ID and optional thread root event ID into a
    /// <see cref="ChannelAddress"/>.
    /// </summary>
    /// <param name="roomId">Matrix room ID, e.g. <c>!abc123:example.com</c>.</param>
    /// <param name="threadRootEventId">Optional <c>m.thread</c> root event ID.</param>
    /// <returns>The encoded address.</returns>
    /// <exception cref="ArgumentException">The room ID is null, empty, or whitespace.</exception>
    public static ChannelAddress Encode(string roomId, string? threadRootEventId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);

        if (string.IsNullOrWhiteSpace(threadRootEventId))
            return ChannelAddress.From(roomId);

        return ChannelAddress.From(string.Concat(roomId, ThreadSeparator, threadRootEventId));
    }

    /// <summary>
    /// Attempts to decode a Matrix-encoded <see cref="ChannelAddress"/> back to its
    /// <c>(roomId, threadRootEventId?)</c> pair.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> only when no usable room ID can be recovered - an empty
    /// address, or an address whose room segment is blank. An empty thread segment is treated as
    /// "no thread" rather than failing the whole decode, so a legacy or hand-authored binding that
    /// carries a trailing separator still routes to the room root instead of being dropped.
    /// </remarks>
    /// <param name="address">The address to decode.</param>
    /// <param name="roomId">The recovered Matrix room ID.</param>
    /// <param name="threadRootEventId">The recovered thread root event ID, or null.</param>
    /// <returns>Whether a room ID was recovered.</returns>
    public static bool TryDecode(ChannelAddress address, out string roomId, out string? threadRootEventId)
    {
        roomId = string.Empty;
        threadRootEventId = null;

        if (address.IsEmpty)
            return false;

        var value = address.Value;

        // LastIndexOf, not IndexOf: the room ID cannot contain the separator, but defending on the
        // right-hand side keeps the decode total even if a future encoding nests a segment.
        var separatorIndex = value.LastIndexOf(ThreadSeparator, StringComparison.Ordinal);

        if (separatorIndex < 0)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            roomId = value;
            return true;
        }

        var room = value[..separatorIndex];
        if (string.IsNullOrWhiteSpace(room))
            return false;

        var thread = value[(separatorIndex + ThreadSeparator.Length)..];

        roomId = room;
        threadRootEventId = string.IsNullOrWhiteSpace(thread) ? null : thread;
        return true;
    }
}
