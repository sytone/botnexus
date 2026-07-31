namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Derives human-meaningful, bounded labels for conversation titles (#2528). Two distinct problems
/// are solved here, both of which the portal Activity table hit:
/// <list type="number">
///   <item>
///     A conversation whose title is a raw routing/session token (for example
///     <c>servicebus:a:1lexPcP4_GM…</c>) is not a title at all. Truncating it only produces a
///     shorter meaningless string, so such a title is <em>replaced</em> by a derived label built
///     from the channel scheme, the owning agent and a short id.
///   </item>
///   <item>
///     Any title - derived or genuine - is capped in length so the DOM can never carry an
///     unbounded single token. CSS ellipsis does the visual truncation; this cap is the structural
///     guarantee that survives a CSS regression.
///   </item>
/// </list>
/// Lives in <c>.Core</c> (per #2452) so the mobile surface can reuse the same rules rather than
/// re-deriving them.
/// </summary>
public static class ConversationLabel
{
    /// <summary>
    /// Maximum number of characters a rendered title may carry into the DOM. Chosen well above any
    /// realistic column width so the ellipsis a reader sees is the CSS one, while still bounding a
    /// pathological 4 KB routing token.
    /// </summary>
    public const int MaxTitleLength = 120;

    /// <summary>
    /// Length at or above which an unbroken, whitespace-free, id-shaped token is treated as an
    /// opaque identifier rather than a title. Short slugs such as <c>build-fix</c> or
    /// <c>cron:nightly</c> stay under this and are left alone.
    /// </summary>
    private const int OpaqueTokenLength = 32;

    /// <summary>
    /// Whether <paramref name="title"/> looks like a raw routing/session identifier rather than a
    /// human title. True when the value has no whitespace, is made only of identifier characters,
    /// and is either long overall or carries a long <c>scheme:token</c> segment.
    /// </summary>
    /// <param name="title">Candidate title text.</param>
    public static bool IsOpaqueIdentifier(string? title)
    {
        var value = PortalText.SingleLine(title);
        if (value.Length == 0)
            return false;

        foreach (var ch in value)
        {
            // Any whitespace means a human wrote (or a summariser produced) prose - not an id.
            if (char.IsWhiteSpace(ch))
                return false;
            if (!(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':' or '/' or '+' or '='))
                return false;
        }

        if (value.Length >= OpaqueTokenLength)
            return true;

        // A shorter value is still opaque if one colon-delimited segment is itself token-shaped.
        foreach (var segment in value.Split(':'))
        {
            if (segment.Length >= OpaqueTokenLength)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Caps <paramref name="value"/> at <paramref name="maxLength"/> characters, replacing the tail
    /// with a single-character ellipsis. Values already within the cap are returned unchanged
    /// (normalised to a single line).
    /// </summary>
    /// <param name="value">Text to bound.</param>
    /// <param name="maxLength">Inclusive maximum length of the result, including the ellipsis.</param>
    public static string Truncate(string? value, int maxLength = MaxTitleLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        var normalized = PortalText.SingleLine(value);
        if (normalized.Length <= maxLength)
            return normalized;

        return string.Concat(normalized.AsSpan(0, maxLength - 1), "\u2026");
    }

    /// <summary>
    /// Builds a readable stand-in for a conversation with no meaningful title, from the channel
    /// scheme embedded in the identifier, the owning agent, and a short id suffix. Deterministic and
    /// clock-free so it is stable across renders and unit-testable.
    /// </summary>
    /// <param name="conversationId">The routing/conversation identifier.</param>
    /// <param name="owningAgentId">The agent that owns the conversation.</param>
    public static string DerivedLabel(string? conversationId, string? owningAgentId)
    {
        var id = PortalText.SingleLine(conversationId);
        var agent = PortalText.SingleLine(owningAgentId);

        var scheme = string.Empty;
        var colon = id.IndexOf(':');
        if (colon > 0)
            scheme = id[..colon];

        var shortId = ShortId(id);

        var parts = new List<string>(3);
        if (scheme.Length > 0)
            parts.Add(scheme);
        if (agent.Length > 0)
            parts.Add(agent);
        if (shortId.Length > 0)
            parts.Add(shortId);

        return parts.Count == 0 ? "(untitled)" : string.Join(" \u00b7 ", parts);
    }

    /// <summary>
    /// The title a row should render: the original when it is human-meaningful, otherwise a derived
    /// label; always length-bounded.
    /// </summary>
    /// <param name="title">The raw conversation title.</param>
    /// <param name="conversationId">The routing/conversation identifier, used when deriving.</param>
    /// <param name="owningAgentId">The owning agent id, used when deriving.</param>
    public static string DisplayTitle(string? title, string? conversationId, string? owningAgentId)
    {
        var normalized = PortalText.SingleLine(title);
        if (normalized.Length == 0)
            return Truncate(DerivedLabel(conversationId, owningAgentId));

        // An opaque title IS a routing key, and it carries the channel scheme that the internal
        // conversation id may lack - so derive from the title itself in that case.
        if (IsOpaqueIdentifier(normalized))
            return Truncate(DerivedLabel(normalized, owningAgentId));

        return Truncate(normalized);
    }

    /// <summary>Last meaningful 8 characters of an identifier, ignoring the scheme prefix.</summary>
    private static string ShortId(string id)
    {
        if (id.Length == 0)
            return string.Empty;

        var tail = id;
        var lastColon = id.LastIndexOf(':');
        if (lastColon >= 0 && lastColon < id.Length - 1)
            tail = id[(lastColon + 1)..];

        return tail.Length <= 8 ? tail : tail[^8..];
    }
}
