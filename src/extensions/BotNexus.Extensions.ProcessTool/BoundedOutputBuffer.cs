using System.Globalization;
using System.Text;
using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Text;

namespace BotNexus.Extensions.ProcessTool;

/// <summary>
/// A UTF-8-byte-bounded, tail-retaining output buffer that discloses what the cap discarded (#3704).
/// </summary>
/// <remarks>
/// <para>
/// This is the <c>process</c> half of the retention policy shared with <c>exec</c>. It is a
/// separate type from the inline collection loop in <c>ExecTool</c> because the two retain opposite
/// ends - exec stops appending once full and keeps the head, whereas a long-running child must keep
/// its most recent output and drop the oldest - but both render their disclosure with the single
/// <see cref="OutputRetentionPolicy.FormatTruncationBanner"/>, so the wording and the cap cannot
/// drift apart again.
/// </para>
/// <para>
/// <b>Byte accounting is incremental and real.</b> The previous implementation compared
/// <c>Length * sizeof(char)</c> against the cap, charging every character 2 bytes. That trimmed
/// ASCII at ~50 KB against a declared 100 KB cap. Bytes are now tracked as
/// <see cref="Encoding.UTF8"/> counts, added on append and subtracted on removal, so the enforced
/// cap is the documented cap without rescanning the whole buffer on every line.
/// </para>
/// </remarks>
internal sealed class BoundedOutputBuffer
{
    private readonly StringBuilder _buffer = new();
    private readonly int _maxOutputBytes;

    public BoundedOutputBuffer(int maxOutputBytes = OutputRetentionPolicy.MaxOutputBytes)
    {
        _maxOutputBytes = maxOutputBytes;
    }

    /// <summary>UTF-8 bytes currently held in the buffer.</summary>
    public long RetainedBytes { get; private set; }

    /// <summary>UTF-8 bytes produced by the child but dropped by the cap. Never decreases.</summary>
    public long DiscardedBytes { get; private set; }

    /// <summary>Appends a line plus the platform newline, then enforces the cap.</summary>
    public void AppendLine(string value)
    {
        _buffer.AppendLine(value);
        RetainedBytes += Encoding.UTF8.GetByteCount(value) + Encoding.UTF8.GetByteCount(Environment.NewLine);
        Trim();
    }

    /// <summary>
    /// Appends text verbatim with no newline. Used by tests to drive the unbroken-line shape that
    /// forces the fallback cut - the exact case that could previously split a surrogate pair.
    /// </summary>
    public void AppendChunk(string value)
    {
        _buffer.Append(value);
        RetainedBytes += Encoding.UTF8.GetByteCount(value);
        Trim();
    }

    /// <summary>The retained text with no disclosure, for callers that measure the buffer itself.</summary>
    public string RawSnapshot() => _buffer.ToString();

    /// <summary>
    /// The shared retention banner when - and only when - the cap actually discarded something.
    /// Empty for an untruncated buffer, which must stay byte-identical to its pre-#3704 form.
    /// </summary>
    public string FormatBanner() =>
        DiscardedBytes <= 0
            ? string.Empty
            : OutputRetentionPolicy.FormatTruncationBanner(RetainedBytes, DiscardedBytes, RetainedOutputPortion.Tail);

    /// <summary>
    /// The retained text, led by the shared retention banner when - and only when - the cap actually
    /// discarded something. An untruncated buffer is returned byte-identically (#3704 AC5).
    /// </summary>
    public string Snapshot()
    {
        var text = _buffer.ToString();
        var banner = FormatBanner();
        return banner.Length == 0 ? text : $"{banner}\n{text}";
    }

    private void Trim()
    {
        if (RetainedBytes <= _maxOutputBytes)
        {
            return;
        }

        var overage = RetainedBytes - _maxOutputBytes;

        // Only the head is examined. Materialising the whole buffer on every line would make the
        // cap cost O(buffer) per line for a chatty child - the reason this scan is bounded by the
        // overage plus one line rather than by the buffer length.
        var headLength = Math.Min(_buffer.Length, (int)Math.Min(int.MaxValue, overage) + MaxLineScan);
        var head = new char[headLength];
        _buffer.CopyTo(0, head, 0, headLength);
        var headSpan = head.AsSpan();

        // Locate the first char index whose removal sheds at least the overage in real UTF-8 bytes.
        // The walk advances by whole grapheme clusters, so the cut index is inherently a safe
        // boundary and this code performs NO surrogate inspection of its own (#2924): the boundary
        // policy stays sole-sourced from GraphemeSafeTruncation / StringInfo.
        var cut = 0;
        long shed = 0;
        while (cut < headSpan.Length && shed < overage)
        {
            var width = StringInfo.GetNextTextElementLength(headSpan[cut..]);
            if (width <= 0)
            {
                break;
            }

            width = Math.Min(width, headSpan.Length - cut);
            shed += Encoding.UTF8.GetByteCount(headSpan.Slice(cut, width));
            cut += width;
        }

        // Prefer a line boundary at or after the cut so the retained buffer starts on a whole line.
        // The search is confined to the scanned head; a line longer than MaxLineScan falls through
        // to the grapheme-safe cut, which is precisely the unbroken-line case #3704 is about.
        var newlineIndex = cut < headSpan.Length ? headSpan[cut..].IndexOf('\n') : -1;
        var removeCount = newlineIndex >= 0
            ? cut + newlineIndex + 1
            : GraphemeSafeCut(headSpan, cut);

        if (removeCount <= 0)
        {
            return;
        }

        removeCount = Math.Min(removeCount, _buffer.Length);
        var removedBytes = Encoding.UTF8.GetByteCount(headSpan[..removeCount]);
        DiscardedBytes += removedBytes;
        RetainedBytes -= removedBytes;
        _buffer.Remove(0, removeCount);
    }

    /// <summary>
    /// How far past the required cut a line boundary is searched for. Bounds the per-line cost of
    /// the cap so a long-running chatty child does not pay O(buffer) on every emitted line.
    /// </summary>
    private const int MaxLineScan = 8 * 1024;

    /// <summary>
    /// Resolves the fallback cut onto the product-wide grapheme-cluster boundary policy rather than
    /// a third local rule, and never lands short of <paramref name="desired"/> - cutting earlier
    /// would leave the buffer over its cap, so the boundary is advanced forward to the next cluster.
    /// </summary>
    private static int GraphemeSafeCut(ReadOnlySpan<char> text, int desired)
    {
        var boundary = GraphemeSafeTruncation.FindBoundaryAtOrBefore(text, desired);
        if (boundary >= desired || boundary >= text.Length)
        {
            return boundary;
        }

        var next = StringInfo.GetNextTextElementLength(text[boundary..]);
        return next <= 0 ? desired : Math.Min(boundary + next, text.Length);
    }
}
