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
internal static class CliText
{
    /// <summary>
    /// Renders an untrusted string for terminal display: terminal control sequences are
    /// removed first, then Spectre markup is escaped. <see langword="null"/> becomes empty.
    /// </summary>
    public static string SafeDisplay(string? value)
        => Markup.Escape(AnsiStripper.Strip(value ?? string.Empty));
}
