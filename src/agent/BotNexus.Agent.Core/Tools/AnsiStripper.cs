using System.Text.RegularExpressions;

namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// Strips ANSI and VT escape sequences from text before returning shell output to the model.
/// </summary>
/// <remarks>
/// <para>
/// Shell and process output frequently contains ANSI escape sequences (color codes, cursor
/// movement, spinner animations, etc.). If these reach the model's context, the model will
/// reproduce them verbatim in file writes and tool arguments — which is the root cause of
/// agents copying escape sequences into <c>edit</c> oldText and breaking matches.
/// </para>
/// <para>
/// Covers the full ECMA-48 / VT100 repertoire:
/// <list type="bullet">
///   <item>CSI sequences: <c>ESC [ ... final</c> (including private-mode <c>?</c> prefix, colon params, intermediate bytes)</item>
///   <item>OSC sequences: <c>ESC ] ... BEL</c> or <c>ESC ] ... ST</c></item>
///   <item>DCS / SOS / PM / APC string sequences terminated by ST (<c>ESC \</c>)</item>
///   <item>nF multi-byte escape sequences</item>
///   <item>Fp / Fe / Fs single-byte escape sequences</item>
///   <item>8-bit C1 control characters (<c>0x80</c>–<c>0x9F</c>)</item>
///   <item>8-bit CSI and OSC</item>
/// </list>
/// </para>
/// <para>
/// A fast-path check skips the full regex when no ESC or C1 bytes are present,
/// so clean output passes through with negligible overhead.
/// </para>
/// </remarks>
public static partial class AnsiStripper
{
    // Full ECMA-48 pattern — matches all standard escape sequences.
    [GeneratedRegex(
        @"\x1b(?:\[[\x30-\x3f]*[\x20-\x2f]*[\x40-\x7e]"  // CSI sequence
        + @"|\][\s\S]*?(?:\x07|\x1b\\)"                   // OSC (BEL or ST terminator)
        + @"|[PX^_][\s\S]*?(?:\x1b\\)"                    // DCS/SOS/PM/APC strings
        + @"|[\x20-\x2f]+[\x30-\x7e]"                     // nF escape sequences
        + @"|[\x30-\x7e])"                                 // Fp/Fe/Fs single-byte
        + @"|\x9b[\x30-\x3f]*[\x20-\x2f]*[\x40-\x7e]"    // 8-bit CSI
        + @"|\x9d[\s\S]*?(?:\x07|\x9c)"                   // 8-bit OSC
        + @"|[\x80-\x9f]",                                // Other 8-bit C1 controls
        RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex AnsiPattern();

    // Fast-path: only run full regex if ESC or C1 bytes are present.
    [GeneratedRegex(@"[\x1b\x80-\x9f]", RegexOptions.Compiled)]
    private static partial Regex HasEscapePattern();

    /// <summary>
    /// Removes ANSI escape sequences from <paramref name="text"/>.
    /// Returns the input unchanged (fast path) when no escape bytes are present.
    /// Safe to call on any string — clean text passes through with negligible overhead.
    /// </summary>
    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text) || !HasEscapePattern().IsMatch(text))
            return text;

        return AnsiPattern().Replace(text, string.Empty);
    }
}
