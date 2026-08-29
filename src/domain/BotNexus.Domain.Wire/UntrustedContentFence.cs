using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace BotNexus.Gateway.Abstractions.Text;

/// <summary>
/// The single definition of the untrusted-content envelope fence: how it is rendered, how it is
/// recognised, and how a truncated projection is repaired so an opening fence always has a
/// matching closing one (issue #3628).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the fence carries a per-envelope id.</b> Before #3628 the fences were two compile-time
/// constants in a public repository, so a page serving the literal line
/// <c>--- END UNTRUSTED WEB CONTENT ---</c> closed the envelope that was meant to contain it, and
/// everything it wrote afterwards read as trusted post-envelope text. A delimiter is a parser, and
/// a parser needs an escaping rule; the escaping rule here is an unpredictable id the page cannot
/// guess. The id is drawn from <see cref="RandomNumberGenerator"/> rather than <see cref="Guid"/>
/// or <see cref="Random"/> because guessability is the whole property being bought.
/// </para>
/// <para>
/// <b>Why this type lives in <c>BotNexus.Domain.Wire</c>.</b> Two layers need the fence vocabulary
/// and they are on opposite sides of the dependency graph: the browser extension WRITES the
/// envelope, and the central tool-output byte budget in <c>BotNexus.Agent.Core</c> must not AMPUTATE
/// it. Teaching <c>ToolOutputBudget</c> about <c>BotNexus.Extensions.BrowserTools</c> would invert
/// the layering - a core loop seam would depend on an extension it loads dynamically. Wire is the
/// zero-dependency leaf both sides already reference (BrowserTools transitively via Agent.Core and
/// Domain; Agent.Core via Agent.Providers.Core), so consuming it here adds no assembly to any load
/// context and no new edge. The budget stays generic: it knows only "a fenced region exists", never
/// what a browser snapshot is.
/// </para>
/// <para>
/// <b>One spelling, consumed not restated.</b> <see cref="MarkerPattern"/> is the only place the
/// fence's shape is written down. <c>UntrustedContentSanitizer</c> consumes it to neutralise a
/// forged fence in page text, <c>BrowserSnapshotEnvelope</c> consumes it to render, and
/// <c>ToolOutputBudget</c> consumes it to repair - the same discipline the sanitizer's own remarks
/// demand after #2808, where a second spelling of "what a marker looks like" was the defect.
/// </para>
/// </remarks>
public static class UntrustedContentFence
{
    /// <summary>The opening fence keyword, without decoration or id.</summary>
    public const string BeginKeyword = "BEGIN UNTRUSTED WEB CONTENT";

    /// <summary>The closing fence keyword, without decoration or id.</summary>
    public const string EndKeyword = "END UNTRUSTED WEB CONTENT";

    /// <summary>The rail rendered either side of the keyword.</summary>
    private const string Rail = "---";

    /// <summary>The id token introducer, e.g. <c>id=3f9c...</c>.</summary>
    private const string IdPrefix = "id=";

    /// <summary>
    /// Recognises a COMPLETE fence line of either polarity, with or without an id. Deliberately
    /// tolerant of surrounding whitespace and of a longer or shorter rail so a page cannot evade
    /// neutralisation by padding: the sanitizer must strip anything the model could plausibly read
    /// as a fence, not merely the exact bytes this type emits. The <c>\r?</c> before <c>$</c> is
    /// load-bearing under <see cref="RegexOptions.Multiline"/>, where <c>$</c> anchors before the
    /// <c>\n</c> only - without it a CRLF payload would slip past the whole filter.
    /// </summary>
    public static readonly Regex MarkerPattern = new(
        @"^[ \t]*-{2,}[ \t]*(?<kind>BEGIN|END)[ \t]+UNTRUSTED[ \t]+WEB[ \t]+CONTENT(?:[ \t]+id=(?<id>[0-9a-fA-F]+))?[ \t]*-{2,}[ \t]*\r?$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Mints a fresh, cryptographically unpredictable envelope id (128 bits, lowercase hex).
    /// </summary>
    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Renders a fence line.
    /// </summary>
    /// <param name="closing">
    /// <c>true</c> for the closing fence, <c>false</c> for the opening one. The polarity leads the
    /// signature so this stays a rendering operation over a discriminator rather than a
    /// string-to-string transformation (the #2925 fence).
    /// </param>
    /// <param name="id">The envelope id from <see cref="NewId"/>; empty renders an id-less fence.</param>
    public static string Render(bool closing, string id)
    {
        var keyword = closing ? EndKeyword : BeginKeyword;
        return string.IsNullOrEmpty(id)
            ? $"{Rail} {keyword} {Rail}"
            : $"{Rail} {keyword} {IdPrefix}{id} {Rail}";
    }

    /// <summary>
    /// Finds the index at which a trailing PARTIAL fence begins, or <c>-1</c> when the text does not
    /// end mid-fence.
    /// </summary>
    /// <remarks>
    /// A byte-budget cut lands wherever the budget runs out, which may be halfway through the
    /// closing fence - leaving the model a line like <c>--- END UNTRUSTED WEB CO</c> that reads as
    /// neither content nor a fence. Clipping it back is strictly safer than leaving a fragment an
    /// attacker's following bytes could complete. Returns an <see cref="int"/> index rather than the
    /// clipped string so this remains an analysis, and the caller keeps ownership of the cut.
    /// </remarks>
    public static int PartialFenceStart(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return -1;
        }

        var lineStart = text.LastIndexOf('\n') + 1;
        var lastLine = text[lineStart..];
        if (lastLine.Length == 0 || MarkerPattern.IsMatch(lastLine))
        {
            return -1;
        }

        var trimmed = lastLine.TrimStart(' ', '\t');
        if (trimmed.Length == 0)
        {
            return -1;
        }

        // A truncated fence is by construction a PREFIX of something this type rendered, so prefix
        // containment against both skeletons is an exact test, not a heuristic - it catches a cut
        // inside the keyword and a cut inside the id alike.
        foreach (var skeleton in Skeletons)
        {
            if (skeleton.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(skeleton, StringComparison.OrdinalIgnoreCase))
            {
                return lineStart;
            }
        }

        return -1;
    }

    private static readonly string[] Skeletons =
    [
        $"{Rail} {BeginKeyword} {IdPrefix}",
        $"{Rail} {EndKeyword} {IdPrefix}",
    ];

    /// <summary>
    /// Reports whether <paramref name="text"/> opens an envelope it never closes, yielding the id
    /// the caller must emit to terminate it.
    /// </summary>
    /// <param name="text">The retained, model-visible projection.</param>
    /// <param name="id">The unterminated envelope's id (possibly empty for an id-less fence).</param>
    /// <returns><c>true</c> when a closing fence is owed.</returns>
    /// <remarks>
    /// Returns <see cref="bool"/> with an <c>out</c> id rather than a nullable string so it is a
    /// query over structure, not a string transformation, and so the "no repair needed" answer is
    /// unambiguous at the call site.
    /// </remarks>
    public static bool TryFindUnterminatedFence(string text, out string id)
    {
        id = string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var open = new List<string>();
        foreach (Match match in MarkerPattern.Matches(text))
        {
            var matchedId = match.Groups["id"].Success ? match.Groups["id"].Value : string.Empty;
            if (string.Equals(match.Groups["kind"].Value, "END", StringComparison.OrdinalIgnoreCase))
            {
                // Close the innermost matching open fence. A close whose id matches nothing open is
                // a forgery that survived sanitisation; it must not cancel a real envelope.
                for (var i = open.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(open[i], matchedId, StringComparison.OrdinalIgnoreCase))
                    {
                        open.RemoveRange(i, open.Count - i);
                        break;
                    }
                }

                continue;
            }

            open.Add(matchedId);
        }

        if (open.Count == 0)
        {
            return false;
        }

        id = open[0];
        return true;
    }
}
