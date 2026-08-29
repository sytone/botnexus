using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Text;
using System.Text;

namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// The shared, central UTF-8 byte budget applied to every tool result before it reaches the model
/// (issue #3162).
/// </summary>
/// <remarks>
/// <para>
/// Before #3162 the only size limits on tool output were five private per-tool constants
/// (<c>ExecTool</c>, <c>ReadTool</c>, <c>GrepTool</c>, <c>GlobTool</c>, <c>WebFetchTool</c>), each
/// with its own budget and its own banner text. Any tool that did not opt in -- notably every
/// MCP-bridged tool -- could return an unbounded payload straight into the context window. Safety
/// being opt-in is the wrong polarity for a resource limit, so this type is applied centrally in
/// <see cref="ToolExecutor"/> to every result regardless of origin.
/// </para>
/// <para>
/// This is a <em>backstop beneath</em> the per-tool caps, not a replacement for them. The default
/// is deliberately larger than every existing per-tool cap (the largest is <c>ExecTool</c>'s
/// 100 KiB), so a tool that already bounded its own output never trips this one and no existing
/// per-tool behaviour changes.
/// </para>
/// <para>
/// An oversize result is returned as a <em>bounded successful projection</em>, never as an error
/// and never as a silent drop: the retained prefix is cut on a rune boundary, and a single
/// consistent marker records the omitted byte count and tells the model how to recover. Upstream
/// OpenClaw made the opposite choice first and had to fix it -- "code mode dead-ends on oversized
/// tool results instead of returning bounded output" -- because turning a recoverable situation
/// into a failure gives the model nothing to act on.
/// </para>
/// </remarks>
public static class ToolOutputBudget
{
    /// <summary>
    /// Default UTF-8 byte budget for a single tool result (256 KiB).
    /// </summary>
    /// <remarks>
    /// Chosen to sit above every first-party per-tool cap so this backstop only ever fires for
    /// output nothing else bounded. A non-positive budget disables the cap entirely, matching the
    /// convention already used by <c>ToolResultPersistenceConfig</c> and
    /// <c>ToolInvocationRecordPolicy</c>.
    /// </remarks>
    public const int DefaultMaxBytes = 256 * 1024;

    /// <summary>
    /// The single recovery instruction appended to every centrally truncated result. One wording
    /// across all tools is the point: five different banners meant the model could never learn one
    /// recovery behaviour.
    /// </summary>
    public const string NarrowingGuidance =
        "This result succeeded but was too large to return in full - rerun with a narrower scope, paginate, or select fewer items.";

    /// <summary>
    /// The name of the tool that reads a truncated payload back through its continuation handle.
    /// </summary>
    public const string ContinuationToolName = "tool_output_continue";

    /// <summary>
    /// The single recovery instruction that makes a truncation RECOVERABLE (issue #2760): it names
    /// the handle, so the remaining bytes are reachable rather than lost.
    /// </summary>
    /// <remarks>
    /// This supersedes narrowing as the FIRST thing the model should try. The forensics behind #2760
    /// showed narrowing guidance alone produced identical retries, because the caller had no dial to
    /// turn on the surface it had actually invoked; a handle is always actionable.
    /// </remarks>
    public static string ContinuationGuidance(string handle, long nextOffset)
        => $"Call {ContinuationToolName} with handle=\"{handle}\" and offset={nextOffset} to retrieve the next portion.";

    /// <summary>
    /// Renders the <c>nextLink</c> line surfaced when the oversized payload carried one.
    /// </summary>
    public static string NextLinkNotice(string nextLink)
        => $"[nextLink: {nextLink}]";

    /// <summary>
    /// Applies the budget to a tool result, returning a bounded successful projection when the
    /// result's text content exceeds <paramref name="maxBytes"/> UTF-8 bytes.
    /// </summary>
    /// <param name="result">The result produced by the tool (or by the after-tool-call hook).</param>
    /// <param name="maxBytes">The UTF-8 byte budget. Zero or negative disables the cap.</param>
    /// <returns>
    /// The original instance when it is already within budget, when the cap is disabled, or when
    /// <paramref name="result"/> is null; otherwise a new result carrying the rune-safe prefix plus
    /// one truncation marker block.
    /// </returns>
    /// <remarks>
    /// Only <see cref="AgentToolContentType.Text"/> blocks are measured and cut. An image block is
    /// an opaque encoded payload whose bytes cannot be truncated into something still decodable, so
    /// it is passed through untouched rather than being corrupted into a broken image.
    /// <para>
    /// A null result is passed through rather than throwing. A misbehaving tool can return null, and
    /// the executor's documented current behaviour is to carry that null forward; a size backstop
    /// must never be the thing that converts a tolerated null into a thrown exception.
    /// </para>
    /// </remarks>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(result))]
    public static AgentToolResult? Apply(AgentToolResult? result, int maxBytes = DefaultMaxBytes)
        => Apply(result, maxBytes, ToolOutputContinuationStore.Shared);

    /// <summary>
    /// Applies the budget knowing which tool produced the result, so the remedy sentence can name
    /// dials that tool actually declares (issue #3404).
    /// </summary>
    /// <param name="result">The result produced by the tool (or by the after-tool-call hook).</param>
    /// <param name="maxBytes">The UTF-8 byte budget. Zero or negative disables the cap.</param>
    /// <param name="tool">
    /// The resolved tool, or <c>null</c> when none was resolved. A null tool - or one whose schema
    /// cannot be read - falls open to <see cref="NarrowingGuidance"/>.
    /// </param>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(result))]
    public static AgentToolResult? Apply(AgentToolResult? result, int maxBytes, IAgentTool? tool)
        => Apply(result, maxBytes, ToolOutputContinuationStore.Shared, tool);

    /// <summary>
    /// Applies the budget using an explicit continuation store (issue #2760).
    /// </summary>
    /// <param name="result">The result produced by the tool (or by the after-tool-call hook).</param>
    /// <param name="maxBytes">The UTF-8 byte budget. Zero or negative disables the cap.</param>
    /// <param name="continuationStore">
    /// Where the full payload is retained so the truncated projection can carry a handle. A null
    /// store degrades to the pre-#2760 behaviour - truncation with narrowing guidance only - rather
    /// than throwing, because a missing recovery aid must never be worse than no cap at all.
    /// </param>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(result))]
    public static AgentToolResult? Apply(
        AgentToolResult? result,
        int maxBytes,
        ToolOutputContinuationStore? continuationStore)
        => Apply(result, maxBytes, continuationStore, tool: null);

    /// <summary>
    /// Applies the budget using an explicit continuation store and the invoked tool.
    /// </summary>
    /// <param name="result">The result produced by the tool (or by the after-tool-call hook).</param>
    /// <param name="maxBytes">The UTF-8 byte budget. Zero or negative disables the cap.</param>
    /// <param name="continuationStore">Where the full payload is retained for the handle.</param>
    /// <param name="tool">The resolved tool whose declared parameters shape the remedy (#3404).</param>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(result))]
    public static AgentToolResult? Apply(
        AgentToolResult? result,
        int maxBytes,
        ToolOutputContinuationStore? continuationStore,
        IAgentTool? tool)
    {
        if (result is null || maxBytes <= 0 || result.Content.Count == 0)
        {
            return result;
        }

        var totalTextBytes = 0L;
        foreach (var block in result.Content)
        {
            if (block.Type == AgentToolContentType.Text)
            {
                totalTextBytes += Encoding.UTF8.GetByteCount(block.Value);
            }
        }

        if (totalTextBytes <= maxBytes)
        {
            return result;
        }

        var bounded = new List<AgentToolContent>(result.Content.Count + 1);
        var remaining = maxBytes;
        var omittedBytes = 0L;
        var fullText = new StringBuilder();

        foreach (var block in result.Content)
        {
            if (block.Type != AgentToolContentType.Text)
            {
                bounded.Add(block);
                continue;
            }

            fullText.Append(block.Value);

            var blockBytes = Encoding.UTF8.GetByteCount(block.Value);
            if (blockBytes <= remaining)
            {
                bounded.Add(block);
                remaining -= blockBytes;
                continue;
            }

            var (prefix, retainedBytes) = TakeRuneSafePrefix(block.Value, remaining);

            // #3628 leg 2: the byte cut is structure-blind, so it can land halfway through a fence
            // line and leave the model "--- END UNTRUSTED WEB CO" - neither content nor a
            // delimiter, and completable by whatever bytes follow. Clip the fragment away before
            // anything else looks at the prefix.
            var partialAt = UntrustedContentFence.PartialFenceStart(prefix);
            if (partialAt >= 0)
            {
                prefix = prefix[..partialAt];
                retainedBytes = Encoding.UTF8.GetByteCount(prefix);
            }

            if (prefix.Length > 0)
            {
                bounded.Add(new AgentToolContent(AgentToolContentType.Text, prefix));
            }

            omittedBytes += blockBytes - retainedBytes;
            remaining = 0;
        }

        var complete = fullText.ToString();
        var retainedTotalBytes = totalTextBytes - omittedBytes;

        var marker = new StringBuilder()
            .Append($"[tool output truncated: {omittedBytes} bytes omitted of {totalTextBytes} total] ")
            .Append(ToolOutputRemedy.ForTool(tool));

        // The nextLink is the one field that makes an oversized upstream page recoverable at the
        // SOURCE rather than merely re-readable from our buffer, so it is surfaced even though the
        // body carrying it was cut (AC4).
        var nextLink = TryExtractNextLink(complete);
        if (nextLink is not null)
        {
            marker.Append(' ').Append(NextLinkNotice(nextLink));
        }

        if (continuationStore is not null)
        {
            var handle = continuationStore.Store(complete);
            marker.Append(' ').Append(ContinuationGuidance(handle, retainedTotalBytes));
        }

        bounded.Add(new AgentToolContent(AgentToolContentType.Text, marker.ToString()));

        RepairUnterminatedFence(bounded);

        return result with { Content = bounded };
    }

    /// <summary>
    /// Re-emits a closing fence when the bounded projection opens an untrusted-content envelope it
    /// no longer closes (issue #3628).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The invariant "an opening fence implies a closing fence" was previously owned by nobody:
    /// <c>BrowserSnapshotEnvelope</c> establishes it at wrap time and has no knowledge that a
    /// downstream cut exists, while this seam is a generic byte budget with no knowledge of any
    /// structured payload. Truncating an envelope open hands the model attacker-controlled page text
    /// whose provenance framing has been destroyed - the exact outcome the envelope exists to
    /// prevent - so the budget now owns the repair.
    /// </para>
    /// <para>
    /// <b>Layering.</b> This does NOT couple the agent loop to the browser extension. The fence
    /// vocabulary lives in <see cref="UntrustedContentFence"/> in the zero-dependency
    /// <c>BotNexus.Domain.Wire</c> leaf that this assembly already references transitively; the
    /// budget knows only that a fenced region exists and how to terminate one. Referencing
    /// <c>BotNexus.Extensions.BrowserTools</c> from here would invert the dependency graph - a core
    /// loop seam depending on an extension it loads dynamically - so it is deliberately not done.
    /// </para>
    /// <para>
    /// The repair is appended AFTER the truncation marker so the marker itself sits inside the
    /// envelope it belongs to and cannot be read as trusted narration that the page authored.
    /// </para>
    /// </remarks>
    private static void RepairUnterminatedFence(List<AgentToolContent> bounded)
    {
        var projection = new StringBuilder();
        foreach (var block in bounded)
        {
            if (block.Type == AgentToolContentType.Text)
            {
                projection.Append(block.Value).Append('\n');
            }
        }

        if (!UntrustedContentFence.TryFindUnterminatedFence(projection.ToString(), out var id))
        {
            return;
        }

        bounded.Add(new AgentToolContent(
            AgentToolContentType.Text,
            UntrustedContentFence.Render(closing: true, id)));
    }

    /// <summary>
    /// Extracts an OData/Graph <c>nextLink</c> value from a payload, if one is present.
    /// </summary>
    /// <remarks>
    /// Deliberately a textual scan rather than a JSON parse: the payload reaching this seam is
    /// oversized by definition and may be NDJSON, a partial body, or not JSON at all, so parsing
    /// would fail on exactly the inputs that need the link most. A scan that finds nothing simply
    /// omits the line.
    /// </remarks>
    internal static string? TryExtractNextLink(string text)
    {
        foreach (var key in NextLinkKeys)
        {
            var index = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var cursor = index + key.Length;
            while (cursor < text.Length && (text[cursor] is ' ' or ':' or '"' or '\t'))
            {
                cursor++;
            }

            var end = cursor;
            while (end < text.Length && text[end] is not ('"' or '\n' or '\r'))
            {
                end++;
            }

            var value = text[cursor..end].Trim();
            if (value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static readonly string[] NextLinkKeys = ["\"@odata.nextLink\"", "\"nextLink\""];

    /// <summary>
    /// Returns the longest prefix of <paramref name="value"/> that fits in
    /// <paramref name="maxBytes"/> UTF-8 bytes, cut on a rune boundary.
    /// </summary>
    /// <remarks>
    /// Cutting on a <see cref="Rune"/> boundary is what keeps a CJK character or an emoji (a
    /// surrogate pair) from being sliced mid-sequence, which would otherwise surface to the model as
    /// U+FFFD replacement characters at the cut.
    /// </remarks>
    private static (string Prefix, int RetainedBytes) TakeRuneSafePrefix(string value, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return (string.Empty, 0);
        }

        var span = value.AsSpan();
        var retainedBytes = 0;
        var retainedChars = 0;

        while (retainedChars < span.Length
            && Rune.DecodeFromUtf16(span[retainedChars..], out var rune, out var charsConsumed) == System.Buffers.OperationStatus.Done)
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (retainedBytes + runeBytes > maxBytes)
            {
                break;
            }

            retainedBytes += runeBytes;
            retainedChars += charsConsumed;
        }

        return (new string(span[..retainedChars]), retainedBytes);
    }
}
