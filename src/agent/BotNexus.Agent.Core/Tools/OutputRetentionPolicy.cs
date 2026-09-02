namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// Which end of a capped output stream survived the retention cap.
/// </summary>
/// <remarks>
/// The two tool surfaces retain opposite ends and a caller must be told which one it is holding.
/// <c>exec</c> collects head-first and drops every line after the cap is reached, so the head
/// survives. <c>process</c> keeps a circular buffer of a long-running child and drops the oldest
/// output, so the tail survives. A banner that did not name the portion would be actively
/// misleading on one of the two paths.
/// </remarks>
public enum RetainedOutputPortion
{
    /// <summary>The earliest output survived; later output was discarded.</summary>
    Head,

    /// <summary>The most recent output survived; earlier output was discarded.</summary>
    Tail,
}

/// <summary>
/// The single canonical retention-cap disclosure policy for tool output (#2895, #3704).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>ExecTool</c> and <c>ManagedProcess</c> declared the identical
/// <c>100 * 1024</c> cap and implemented it twice. #2895 added a disclosure banner to the exec copy
/// only; nothing referenced the process copy, so the fix could not propagate and the hardened
/// sibling made the surface look covered when half of it silently dropped output (#3704). Both call
/// sites now resolve to <see cref="FormatTruncationBanner"/>, so a future change to the wording -
/// or to the cap - cannot land on one path alone.
/// </para>
/// <para>
/// The banner deliberately reports the LOSS rather than restating the compile-time cap: the cap is
/// fixed and knowable, whereas how much went missing is not, and one dropped line versus fifty
/// dropped megabytes warrant very different responses from the caller.
/// </para>
/// </remarks>
public static class OutputRetentionPolicy
{
    /// <summary>Maximum bytes of UTF-8 output either tool retains before it must discard.</summary>
    public const int MaxOutputBytes = 100 * 1024;

    /// <summary>
    /// Stable prefix the banner always starts with. Tests assert on the prefix rather than on the
    /// full sentence so the wording can evolve without becoming unrecognisable.
    /// </summary>
    public const string TruncationBannerPrefix = "[output truncated:";

    /// <summary>
    /// Renders the retention-cap banner naming the retained volume, the discarded volume, the total
    /// produced, and which end of the stream survived.
    /// </summary>
    /// <param name="retainedBytes">Bytes actually kept in the returned output.</param>
    /// <param name="discardedBytes">Bytes produced by the child but dropped once the cap was hit.</param>
    /// <param name="retained">Which end of the stream the retained bytes came from.</param>
    public static string FormatTruncationBanner(long retainedBytes, long discardedBytes, RetainedOutputPortion retained)
    {
        var produced = retainedBytes + discardedBytes;
        var keptEnd = retained == RetainedOutputPortion.Head ? "head" : "tail";
        var lostEnd = retained == RetainedOutputPortion.Head ? "tail" : "head";

        return $"{TruncationBannerPrefix} retained {retainedBytes} bytes ({keptEnd}) of {produced} bytes produced, " +
               $"discarded {discardedBytes} bytes ({lostEnd}) at the {MaxOutputBytes / 1024}KB cap]";
    }
}
