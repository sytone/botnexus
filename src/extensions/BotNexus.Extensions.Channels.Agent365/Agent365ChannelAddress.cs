using BotNexus.Domain.Primitives;

namespace BotNexus.Extensions.Channels.Agent365;

/// <summary>
/// Agent 365 encoder/decoder that folds the Agents SDK conversation identity into the opaque
/// <see cref="ChannelAddress"/> the BotNexus router uses.
/// </summary>
/// <remarks>
/// <para>
/// An outbound Activity reply needs two things the core router does not model natively: the
/// conversation id and the <c>serviceUrl</c> the connector must POST the reply to (the SDK keys
/// its connector cache on serviceUrl). Both are folded into the single <see cref="ChannelAddress"/>
/// string so a later <c>SendAsync</c> can reconstruct the reply target without a side channel.
/// </para>
/// <para>
/// The chosen format is <c>&lt;conversationId&gt;|svc:&lt;serviceUrl&gt;</c>. The conversation id is
/// primary; the <c>|svc:</c> suffix is optional and omitted when no serviceUrl is known. The pipe is
/// a safe delimiter because Agents SDK conversation ids are opaque tokens that never contain it,
/// while a serviceUrl always follows the marker verbatim (URLs may contain <c>:</c> and <c>/</c>,
/// which is why the marker is matched only once, at the first occurrence).
/// </para>
/// </remarks>
public static class Agent365ChannelAddress
{
    private const string ServiceUrlSeparator = "|svc:";

    /// <summary>
    /// Encodes an Agents SDK conversation id and optional service URL into a
    /// <see cref="ChannelAddress"/>. When <paramref name="serviceUrl"/> is null or empty the address
    /// is just the bare conversation id.
    /// </summary>
    public static ChannelAddress Encode(string conversationId, string? serviceUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        if (string.IsNullOrWhiteSpace(serviceUrl))
            return ChannelAddress.From(conversationId);

        return ChannelAddress.From(string.Concat(conversationId, ServiceUrlSeparator, serviceUrl));
    }

    /// <summary>
    /// Attempts to decode an Agent 365-encoded <see cref="ChannelAddress"/> back to its
    /// <c>(conversationId, serviceUrl?)</c> pair. Returns <see langword="false"/> only when the
    /// address is empty; a missing service-url segment decodes to a null <paramref name="serviceUrl"/>
    /// rather than failing, because polling-style bindings may carry a bare conversation id.
    /// </summary>
    public static bool TryDecode(ChannelAddress address, out string conversationId, out string? serviceUrl)
    {
        conversationId = string.Empty;
        serviceUrl = null;

        if (address.IsEmpty)
            return false;

        var value = address.Value;
        var separatorIndex = value.IndexOf(ServiceUrlSeparator, StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            conversationId = value;
            return true;
        }

        conversationId = value[..separatorIndex];
        var svc = value[(separatorIndex + ServiceUrlSeparator.Length)..];
        serviceUrl = svc.Length == 0 ? null : svc;
        return conversationId.Length > 0;
    }
}
