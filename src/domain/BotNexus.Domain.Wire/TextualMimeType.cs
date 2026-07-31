using System.Text;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// The SINGLE canonical answer to "is this MIME type textual enough to inline into a prompt?",
/// shared by the client-side content-part builder and the server-side prompt composer.
/// </summary>
/// <remarks>
/// <para>
/// #2568: a <c>.json</c> attachment uploaded from the portal composer reached the agent as a
/// self-closing <c>&lt;attachment ... /&gt;</c> metadata tag with the payload silently discarded.
/// Both ends independently asked the question "is this text?" with the same too-narrow
/// <c>MimeType.StartsWith("text/")</c> test, so <c>application/json</c> took the binary branch on
/// the client and the metadata-only branch on the server.
/// </para>
/// <para>
/// The recurrence pattern matters more than the individual fix. #2294 fixed attachment loss at one
/// call site; #2484/#2494 found it again on three further dispatch paths and extracted
/// <c>AgentUserMessageComposer</c> as the one composition seam; #2568 is the same family a third
/// time because the seam's <em>notion of textual</em> was still duplicated across the wire. Two
/// predicates that can drift are how this comes back a fourth time (compare the five duplicate
/// <c>HashActor</c> copies collapsed by #2515). Hence exactly one predicate, here.
/// </para>
/// <para>
/// PLACEMENT (#2329/#2345): this lives in <c>BotNexus.Domain.Wire</c> because that is the only
/// assembly BOTH the Blazor WebAssembly client and the gateway may legally reference. The client's
/// dependency graph is fenced by <c>WasmPayloadDependencyArchitectureTests</c> - every assembly
/// reachable from a WASM entry point is downloaded by the browser - and this project is allowlisted
/// there strictly on the condition that it stays dependency-free. This type honours that: framework
/// types only, no <c>PackageReference</c>, no <c>ProjectReference</c>.
/// </para>
/// </remarks>
public static class TextualMimeType
{
    /// <summary>
    /// Maximum number of UTF-8 bytes of attachment payload inlined verbatim into a prompt before
    /// truncation.
    /// </summary>
    /// <remarks>
    /// 256 KiB. Rationale: large enough that the realistic analysis payloads this issue is about
    /// (config files, API responses, logs, CSV exports) arrive whole, and small enough that a single
    /// attachment cannot dominate a context window. 256 KiB of text is roughly 64k-85k tokens, which
    /// already approaches the working budget of a mid-size model; anything materially larger would
    /// convert a dropped-payload bug into a blown-context bug, and session compaction is already
    /// under strain on large sessions (#2522/#2556) - an unbounded 7 MB paste could make a session
    /// uncompactable, which is strictly worse than the defect being fixed. When the bound is hit the
    /// content is cut and <see cref="TruncationMarker"/> is appended, so the truncation is explicit
    /// to the agent rather than an invisible short read.
    /// </remarks>
    public const int MaxInlineBytes = 256 * 1024;

    /// <summary>
    /// Marker appended to inlined content when <see cref="MaxInlineBytes"/> is exceeded. Public so
    /// tests assert this exact string rather than "something was truncated".
    /// </summary>
    public const string TruncationMarker =
        "\n[... attachment truncated: only the first 262144 bytes are shown ...]";

    /// <summary>
    /// MIME types outside the <c>text/</c> tree whose payloads are plain text and therefore useful
    /// inlined into a prompt. Kept explicit rather than heuristic so widening it is a deliberate,
    /// reviewable act.
    /// </summary>
    private static readonly HashSet<string> TextualApplicationTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "application/json",
            "application/ld+json",
            "application/xml",
            "application/xhtml+xml",
            "application/javascript",
            "application/ecmascript",
            "application/x-javascript",
            "application/typescript",
            "application/x-typescript",
            "application/x-yaml",
            "application/yaml",
            "application/x-sh",
            "application/x-shellscript",
            "application/sql",
            "application/x-sql",
            "application/graphql",
            "application/toml",
            "application/x-toml",
            "application/x-ndjson",
            "application/x-httpd-php",
            "application/x-latex",
            "application/x-tex",
            "application/csv",
            "application/x-csv",
            "application/rtf",
            "application/x-www-form-urlencoded",
        };

    /// <summary>
    /// Structured-syntax suffixes (RFC 6839) that guarantee a textual representation regardless of
    /// the base type, e.g. <c>application/vnd.api+json</c> or <c>image/svg+xml</c>.
    /// </summary>
    private static readonly string[] TextualStructuredSuffixes =
        ["+json", "+xml", "+yaml", "+ld+json"];

    /// <summary>
    /// True when <paramref name="mimeType"/> denotes content that can be inlined into a prompt as
    /// text. Image types are excluded unconditionally: they travel the vision path and this
    /// predicate must never divert them (notably <c>image/svg+xml</c>, whose <c>+xml</c> suffix
    /// would otherwise match).
    /// </summary>
    public static bool IsTextual(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
            return false;

        // Strip any parameters: "application/json; charset=utf-8" -> "application/json".
        var separator = mimeType.IndexOf(';', StringComparison.Ordinal);
        var essence = (separator >= 0 ? mimeType[..separator] : mimeType).Trim();

        if (essence.Length == 0)
            return false;

        // Images are the vision path's business and are never inlined as text (#2568 must not
        // change image handling). This also stops image/svg+xml matching the +xml suffix below.
        if (essence.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return false;

        if (essence.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return true;

        if (TextualApplicationTypes.Contains(essence))
            return true;

        foreach (var suffix in TextualStructuredSuffixes)
        {
            if (essence.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Decodes <paramref name="data"/> as UTF-8, bounded by <see cref="MaxInlineBytes"/>. When the
    /// bound is hit the returned text carries <see cref="TruncationMarker"/> and
    /// <paramref name="truncated"/> is set.
    /// </summary>
    public static string DecodeBounded(ReadOnlySpan<byte> data, out bool truncated)
    {
        truncated = data.Length > MaxInlineBytes;
        var slice = truncated ? data[..MaxInlineBytes] : data;
        var text = Encoding.UTF8.GetString(slice);
        return truncated ? text + TruncationMarker : text;
    }

    /// <summary>
    /// Bounds already-decoded text by <see cref="MaxInlineBytes"/> UTF-8 bytes, appending
    /// <see cref="TruncationMarker"/> when the bound is hit. Used for parts that arrive as text
    /// (the client decodes textual attachments before transport) so the SAME bound applies whether
    /// the payload arrived decoded or as bytes.
    /// </summary>
    public static string BoundText(string? text, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        if (Encoding.UTF8.GetByteCount(text) <= MaxInlineBytes)
            return text;

        return DecodeBounded(Encoding.UTF8.GetBytes(text), out truncated);
    }
}
