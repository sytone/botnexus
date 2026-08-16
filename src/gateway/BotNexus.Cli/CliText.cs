using System.Text.RegularExpressions;
using BotNexus.Agent.Core.Tools;
using Spectre.Console;

namespace BotNexus.Cli;

/// <summary>
/// The single definition of "safe to hand to Spectre" for the CLI.
/// </summary>
/// <remarks>
/// <para>
/// Strings the CLI renders come from config files, the cron SQLite store, and raw
/// caller input - all of which are agent-writable. <c>Markup.Escape</c> only neutralises
/// Spectre's own <c>[]</c> markup grammar; it happily forwards ANSI/OSC/DCS control
/// sequences to the terminal, so a stored value containing OSC-52 can set the operator's
/// clipboard or overwrite earlier output (issue #2722).
/// </para>
/// <para>
/// Stripping escape <i>sequences</i> is necessary but not sufficient. <see cref="AnsiStripper"/>
/// removes well-formed CSI/OSC/DCS sequences and the 8-bit C1 range, but a stored value can
/// carry <b>lone</b> C0 controls that never formed a sequence - a bare <c>BEL</c> (0x07) that
/// rings the operator's terminal, a <c>BS</c> (0x08) or <c>CR</c> (0x0d) that reposition the
/// cursor to overwrite text already printed, or a <c>DEL</c> (0x7f). Those survive the
/// sequence stripper by construction, so <see cref="SafeDisplay"/> scrubs them as a second
/// step (issue #3208).
/// </para>
/// <para>
/// Every render site must compose through <see cref="SafeDisplay"/> rather than spelling
/// out its own combination of stripping and escaping. Two spellings of "safe" is the
/// defect this helper exists to remove.
/// </para>
/// <para>
/// The strip primitive is <see cref="AnsiStripper"/> from <c>BotNexus.Agent.Core</c>,
/// deliberately reused rather than copied. A second implementation also exists inside the
/// SignalR Blazor client, but that one is a client-side asset in a project the CLI does not
/// (and should not) reference; Agent.Core is already on the CLI's reference graph via
/// <c>BotNexus.Gateway</c> and is the canonical server-side definition. Adding a third copy
/// here would recreate exactly the drift this fix removes.
/// </para>
/// </remarks>
internal static partial class CliText
{
    /// <summary>
    /// Lone C0 controls and DEL that survive <see cref="AnsiStripper"/> because they never
    /// formed an escape sequence. <c>\t</c> (0x09) and <c>\n</c> (0x0a) are deliberately
    /// preserved: they are ordinary layout characters, not terminal-state manipulation.
    /// </summary>
    [GeneratedRegex(@"[\x00-\x08\x0b-\x1f\x7f]", RegexOptions.Compiled)]
    private static partial Regex ResidualControls();

    /// <summary>
    /// Renders an untrusted string for terminal display: terminal control sequences are
    /// removed first, then any lone control characters they left behind, then Spectre markup
    /// is escaped. <see langword="null"/> becomes empty.
    /// </summary>
    /// <remarks>
    /// Display-only. Machine-readable output (<c>--json</c> and friends) must NOT compose
    /// through this helper - sanitisation would change the payload bytes (#3208 AC4).
    /// </remarks>
    public static string SafeDisplay(string? value)
        => Markup.Escape(ResidualControls().Replace(AnsiStripper.Strip(value ?? string.Empty), string.Empty));
}
