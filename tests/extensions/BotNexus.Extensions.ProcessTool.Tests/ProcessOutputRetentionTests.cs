using System.Text;
using BotNexus.Agent.Core.Tools;

namespace BotNexus.Extensions.ProcessTool.Tests;

/// <summary>
/// Issue #3704: the process retention cap dropped the head of the buffer with no disclosure,
/// measured the cap as <c>Length * sizeof(char)</c> (so ASCII trimmed at ~50 KB, not the declared
/// 100 KB), and its no-newline fallback cut at an arbitrary UTF-16 index that could split a
/// surrogate pair.
/// </summary>
public class ProcessOutputRetentionTests
{
    /// <summary>
    /// AC3. Multi-byte UTF-8 output must be measured in real UTF-8 bytes, so the buffer retains
    /// close to the declared 100 KB cap. The old accounting counted UTF-16 code units at 2 bytes
    /// each, which is the wrong quantity in both directions.
    /// </summary>
    [Fact]
    public void Retention_WithMultiByteUtf8_RetainsCloseToDeclaredCap()
    {
        var buffer = new BoundedOutputBuffer();

        // 'é' is 1 UTF-16 code unit but 2 UTF-8 bytes: the old char-count accounting over-measures
        // it by exactly the amount that made ASCII under-measured.
        var line = new string('é', 200);
        for (var i = 0; i < 2_000; i++)
        {
            buffer.AppendLine(line);
        }

        var retained = Encoding.UTF8.GetByteCount(buffer.RawSnapshot());

        retained.ShouldBeLessThanOrEqualTo(OutputRetentionPolicy.MaxOutputBytes);
        retained.ShouldBeGreaterThan(
            (int)(OutputRetentionPolicy.MaxOutputBytes * 0.9),
            "the buffer must retain close to the declared 100 KB, not a fraction of it");
    }

    /// <summary>
    /// AC3, the ASCII half. Under the old accounting ASCII trimmed at ~50 KB because every char was
    /// charged 2 bytes.
    /// </summary>
    [Fact]
    public void Retention_WithAscii_RetainsCloseToDeclaredCap()
    {
        var buffer = new BoundedOutputBuffer();
        var line = new string('a', 400);
        for (var i = 0; i < 2_000; i++)
        {
            buffer.AppendLine(line);
        }

        var retained = Encoding.UTF8.GetByteCount(buffer.RawSnapshot());

        retained.ShouldBeLessThanOrEqualTo(OutputRetentionPolicy.MaxOutputBytes);
        retained.ShouldBeGreaterThan(
            (int)(OutputRetentionPolicy.MaxOutputBytes * 0.9),
            "ASCII must not trim at half the declared cap");
    }

    /// <summary>
    /// AC4. A single unbroken line of astral-plane characters - no '\n' anywhere after the trim
    /// index, which is exactly the shape that drives the fallback cut - must never leave an
    /// unpaired surrogate at the start of the retained buffer.
    /// </summary>
    [Fact]
    public void Retention_AstralCharsAcrossTrimBoundaryWithNoNewline_LeavesNoUnpairedSurrogate()
    {
        var buffer = new BoundedOutputBuffer();

        // U+1D160 is a surrogate pair. AppendChunk adds no trailing newline, so the whole buffer is
        // one unbroken line and the newline search after `excess` finds nothing.
        var chunk = string.Concat(Enumerable.Repeat("\U0001D160", 4_000));
        for (var i = 0; i < 40; i++)
        {
            buffer.AppendChunk(chunk);
        }

        var retained = buffer.RawSnapshot();

        retained.Length.ShouldBeGreaterThan(0);
        HasUnpairedSurrogate(retained).ShouldBeFalse(
            "the fallback cut must not sever a surrogate pair");
    }

    /// <summary>
    /// AC1 + AC2. Once the cap has dropped output, the caller must be told how much went missing,
    /// in the wording produced by the single shared policy helper - not a second banner.
    /// </summary>
    [Fact]
    public void Retention_AfterTrimming_DisclosesDiscardedBytesUsingSharedBanner()
    {
        var buffer = new BoundedOutputBuffer();
        var line = new string('a', 400);
        for (var i = 0; i < 2_000; i++)
        {
            buffer.AppendLine(line);
        }

        buffer.DiscardedBytes.ShouldBeGreaterThan(0);

        var disclosed = buffer.Snapshot();
        var expectedBanner = OutputRetentionPolicy.FormatTruncationBanner(
            buffer.RetainedBytes,
            buffer.DiscardedBytes,
            RetainedOutputPortion.Tail);

        disclosed.ShouldStartWith(expectedBanner);
        disclosed.ShouldContain($"discarded {buffer.DiscardedBytes} bytes");
        disclosed.ShouldContain("(tail)");
    }

    /// <summary>
    /// AC5. An untruncated buffer must be byte-identical to the pre-change behaviour: no banner,
    /// no annotation of any kind.
    /// </summary>
    [Fact]
    public void Retention_WithoutTrimming_IsByteIdenticalAndUnbannered()
    {
        var buffer = new BoundedOutputBuffer();
        buffer.AppendLine("first");
        buffer.AppendLine("second");

        var expected = "first" + Environment.NewLine + "second" + Environment.NewLine;

        buffer.DiscardedBytes.ShouldBe(0);
        buffer.Snapshot().ShouldBe(expected);
        buffer.Snapshot().Contains(OutputRetentionPolicy.TruncationBannerPrefix, StringComparison.Ordinal)
            .ShouldBeFalse("output below the cap must not be annotated at all");
        buffer.Snapshot().Contains("truncated", StringComparison.OrdinalIgnoreCase)
            .ShouldBeFalse("output below the cap must not be annotated at all");
    }

    private static bool HasUnpairedSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }
}
