using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Api.Controllers;

namespace BotNexus.Gateway.Api.Export;

/// <summary>
/// Names the contiguous first and last entry of a partial-range export (issue #3279, acceptance
/// criterion 1).
/// </summary>
/// <remarks>
/// <para>
/// Endpoints are <see cref="ConversationHistoryEntry.EntryId"/> values - the stable
/// <c>{sessionId}#{ordinal}</c> keys the history projection stamps onto every entry it emits. That
/// is deliberate: the selector is resolved against exactly the assembled history the full export
/// renders, so a caller can only name entries that actually exist in the document being exported,
/// and an endpoint carrying a session id belonging to some other conversation is detectable rather
/// than silently coerced into an index.
/// </para>
/// <para>
/// Positional indices were rejected for this contract. An index means something different after any
/// compaction, fold or filtering change, so a saved or shared range would silently start naming
/// different entries; an entry key does not.
/// </para>
/// </remarks>
/// <param name="FirstEntryId">The entry id of the first included entry.</param>
/// <param name="LastEntryId">The entry id of the last included entry.</param>
public sealed record ExportRangeSelector(string FirstEntryId, string LastEntryId);

/// <summary>
/// The specific reasons a range selector can be rejected (issue #3279, acceptance criterion 4).
/// </summary>
/// <remarks>
/// Each member is a distinct, separately reportable failure. Collapsing them into one generic "bad
/// range" would leave a caller unable to tell a typo from a stale link from a cross-conversation
/// mix-up, and - far worse - would invite the clamping implementation the acceptance criterion
/// exists to forbid: clamping produces a document whose summary header describes a range the
/// caller never asked for.
/// </remarks>
public enum ExportRangeErrorKind
{
    /// <summary>No error.</summary>
    None = 0,

    /// <summary>The addressed conversation or session does not exist (maps to 404, not 400).</summary>
    SubjectNotFound,

    /// <summary>An endpoint was empty or not a well-formed entry id.</summary>
    MalformedEndpoint,

    /// <summary>An endpoint names a session that belongs to a different conversation.</summary>
    ForeignConversation,

    /// <summary>An endpoint is well-formed but names no entry in the assembled transcript.</summary>
    EndpointNotFound,

    /// <summary>Both endpoints exist but the last one precedes the first in assembled order.</summary>
    ReversedRange
}

/// <summary>
/// The outcome of a ranged export assembly: either a document, or a specific rejection (issue
/// #3279, acceptance criteria 1 and 4).
/// </summary>
/// <remarks>
/// A result type rather than an out-parameter or an exception, because the failure modes here are
/// ordinary caller errors that the route must turn into distinct HTTP responses - not exceptional
/// conditions - and because returning <c>null</c> for "bad range" would make the four rejection
/// reasons indistinguishable at the route boundary.
/// </remarks>
public sealed record ExportRangeResult
{
    private ExportRangeResult() { }

    /// <summary>Gets the assembled excerpt document, or <see langword="null"/> when the range was rejected.</summary>
    public ExportDocument? Document { get; private init; }

    /// <summary>Gets the rejection reason; <see cref="ExportRangeErrorKind.None"/> on success.</summary>
    public ExportRangeErrorKind Error { get; private init; }

    /// <summary>Gets the stable machine-readable error code, or <see langword="null"/> on success.</summary>
    public string? ErrorCode { get; private init; }

    /// <summary>Gets the human-readable rejection message, or <see langword="null"/> on success.</summary>
    public string? Message { get; private init; }

    /// <summary>Gets whether the range was resolved.</summary>
    public bool IsSuccess => Error == ExportRangeErrorKind.None && Document is not null;

    /// <summary>Creates a successful result.</summary>
    /// <param name="document">The assembled excerpt.</param>
    /// <returns>The result.</returns>
    public static ExportRangeResult Success(ExportDocument document)
        => new() { Document = document };

    /// <summary>Creates a "subject does not exist" result, which routes map to 404.</summary>
    /// <returns>The result.</returns>
    public static ExportRangeResult SubjectNotFound()
        => new()
        {
            Error = ExportRangeErrorKind.SubjectNotFound,
            ErrorCode = "not_found",
            Message = "The requested conversation or session does not exist."
        };

    /// <summary>Creates a malformed-endpoint rejection.</summary>
    /// <param name="which">Which endpoint was malformed (<c>first</c> or <c>last</c>).</param>
    /// <param name="value">The offending value.</param>
    /// <returns>The result.</returns>
    public static ExportRangeResult MalformedEndpoint(string which, string? value)
        => new()
        {
            Error = ExportRangeErrorKind.MalformedEndpoint,
            ErrorCode = "range_endpoint_malformed",
            Message = $"The '{which}' range endpoint is not a well-formed entry id: '{value}'."
        };

    /// <summary>Creates a cross-conversation rejection.</summary>
    /// <param name="which">Which endpoint was foreign (<c>first</c> or <c>last</c>).</param>
    /// <param name="value">The offending entry id.</param>
    /// <returns>The result.</returns>
    public static ExportRangeResult ForeignConversation(string which, string value)
        => new()
        {
            Error = ExportRangeErrorKind.ForeignConversation,
            ErrorCode = "range_endpoint_foreign_conversation",
            Message =
                $"The '{which}' range endpoint '{value}' names a session that belongs to a different " +
                "conversation than the one being exported."
        };

    /// <summary>Creates a non-existent-endpoint rejection.</summary>
    /// <param name="which">Which endpoint was missing (<c>first</c> or <c>last</c>).</param>
    /// <param name="value">The offending entry id.</param>
    /// <returns>The result.</returns>
    public static ExportRangeResult EndpointNotFound(string which, string value)
        => new()
        {
            Error = ExportRangeErrorKind.EndpointNotFound,
            ErrorCode = "range_endpoint_not_found",
            Message = $"The '{which}' range endpoint '{value}' does not name an entry in this transcript."
        };

    /// <summary>Creates a reversed-range rejection.</summary>
    /// <param name="first">The first endpoint.</param>
    /// <param name="last">The last endpoint.</param>
    /// <returns>The result.</returns>
    public static ExportRangeResult Reversed(string first, string last)
        => new()
        {
            Error = ExportRangeErrorKind.ReversedRange,
            ErrorCode = "range_reversed",
            Message =
                $"The range endpoints are reversed: '{last}' precedes '{first}' in assembled order. " +
                "The range is not clamped or re-ordered; supply the endpoints in transcript order."
        };
}

/// <summary>
/// Resolves an <see cref="ExportRangeSelector"/> against an assembled entry list (issue #3279).
/// </summary>
/// <remarks>
/// This type performs no clamping whatsoever. Every out-of-contract selector produces a rejection
/// carrying its own reason; there is no code path that trims an endpoint to the nearest valid entry
/// and returns a document. That is the whole point of acceptance criterion 4 - a clamped export
/// renders a summary header that misdescribes its own contents.
/// </remarks>
public static class ExportRangeResolver
{
    /// <summary>
    /// Locates the inclusive index bounds of the selector within <paramref name="entries"/>.
    /// </summary>
    /// <param name="entries">The assembled transcript entries, in order.</param>
    /// <param name="range">The selector.</param>
    /// <param name="foreignSessionIds">
    /// Session ids known to exist but not belonging to the addressed subject; an endpoint naming one
    /// of these is reported as <see cref="ExportRangeErrorKind.ForeignConversation"/> rather than as
    /// a plain missing endpoint.
    /// </param>
    /// <param name="firstIndex">The resolved inclusive start index.</param>
    /// <param name="lastIndex">The resolved inclusive end index.</param>
    /// <returns><see langword="null"/> on success, or the rejection to return.</returns>
    public static ExportRangeResult? TryResolve(
        IReadOnlyList<ConversationHistoryEntry> entries,
        ExportRangeSelector range,
        IReadOnlySet<string> foreignSessionIds,
        out int firstIndex,
        out int lastIndex)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(foreignSessionIds);

        firstIndex = -1;
        lastIndex = -1;

        var malformed = Validate("first", range.FirstEntryId) ?? Validate("last", range.LastEntryId);
        if (malformed is not null)
            return malformed;

        var resolvedFirst = IndexOf(entries, range.FirstEntryId);
        if (resolvedFirst < 0)
            return Missing("first", range.FirstEntryId, foreignSessionIds);

        var resolvedLast = IndexOf(entries, range.LastEntryId);
        if (resolvedLast < 0)
            return Missing("last", range.LastEntryId, foreignSessionIds);

        // Reversed endpoints are rejected, never swapped. Silently normalising them would make an
        // ambiguous request succeed with a range the caller did not ask for.
        if (resolvedLast < resolvedFirst)
            return ExportRangeResult.Reversed(range.FirstEntryId, range.LastEntryId);

        firstIndex = resolvedFirst;
        lastIndex = resolvedLast;
        return null;
    }

    private static ExportRangeResult? Validate(string which, string? value)
        => string.IsNullOrWhiteSpace(value) || !ExportEntryId.IsWellFormed(value)
            ? ExportRangeResult.MalformedEndpoint(which, value)
            : null;

    private static ExportRangeResult Missing(string which, string value, IReadOnlySet<string> foreignSessionIds)
        => ExportEntryId.TryGetSessionId(value, out var sessionId) && foreignSessionIds.Contains(sessionId)
            ? ExportRangeResult.ForeignConversation(which, value)
            : ExportRangeResult.EndpointNotFound(which, value);

    private static int IndexOf(IReadOnlyList<ConversationHistoryEntry> entries, string entryId)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (string.Equals(entries[i].EntryId, entryId, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }
}

/// <summary>
/// Format helpers for the stable <c>{sessionId}#{ordinal}</c> entry id stamped by
/// <see cref="ConversationHistoryProjection"/> (issue #3279).
/// </summary>
public static class ExportEntryId
{
    /// <summary>The separator between the session id and the per-session ordinal.</summary>
    public const char Separator = '#';

    /// <summary>Builds the entry id for a session and per-session ordinal.</summary>
    /// <param name="sessionId">The owning session id.</param>
    /// <param name="ordinal">The zero-based ordinal of the entry within that session's emitted run.</param>
    /// <returns>The entry id.</returns>
    /// <remarks>
    /// Takes the <see cref="SessionId"/> value object rather than a raw string so the id-minting
    /// seam cannot be handed an arbitrary identifier: the entry id is a persisted, shareable
    /// deep-link key, and a wrong-kind id here would only surface as an unresolvable link later.
    /// </remarks>
    public static string Build(SessionId sessionId, int ordinal) => $"{sessionId.Value}{Separator}{ordinal}";

    /// <summary>Gets whether a value has the shape of an entry id.</summary>
    /// <param name="value">The candidate value.</param>
    /// <returns><see langword="true"/> when well formed.</returns>
    public static bool IsWellFormed(string? value) => TryGetSessionId(value, out _);

    /// <summary>Extracts the session id portion of an entry id.</summary>
    /// <param name="value">The entry id.</param>
    /// <param name="sessionId">The session id when well formed.</param>
    /// <returns><see langword="true"/> when well formed.</returns>
    public static bool TryGetSessionId(string? value, out string sessionId)
    {
        sessionId = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        // Session ids are opaque and could in principle contain the separator, so split on the LAST
        // one: the ordinal suffix is unambiguous, the prefix is whatever remains.
        var index = value.LastIndexOf(Separator);
        if (index <= 0 || index == value.Length - 1)
            return false;

        if (!int.TryParse(value.AsSpan(index + 1), out var ordinal) || ordinal < 0)
            return false;

        sessionId = value[..index];
        return true;
    }
}

/// <summary>
/// The document heading for each <see cref="ExportScope"/> (issue #3279).
/// </summary>
/// <remarks>
/// Centralised so the Markdown and HTML projections cannot label the same document differently. A
/// two-branch conditional duplicated in two renderers is exactly how a new scope ends up rendering
/// as "Session Transcript" in one format and something else in the other.
/// </remarks>
public static class ExportHeading
{
    /// <summary>Gets the heading text for a scope.</summary>
    /// <param name="scope">The document scope.</param>
    /// <returns>The heading.</returns>
    public static string For(ExportScope scope) => scope switch
    {
        ExportScope.Conversation => "Conversation Transcript",
        ExportScope.Session => "Session Transcript",
        ExportScope.Excerpt => "Transcript Excerpt",
        _ => "Transcript"
    };
}
