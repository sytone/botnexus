namespace BotNexus.Tools;

/// <summary>
/// Size guardrails for the <see cref="ReadTool"/> (issue #2689).
/// </summary>
/// <remarks>
/// <para>
/// <c>read</c> is the fleet's largest token consumer: 41.9% of its tokens came from 636 calls that
/// each returned more than 20 KB, and a further 1,429 calls re-read a file the same session had
/// already read. Neither is a defect in what <c>read</c> can express - the tool has always accepted
/// <c>offset</c>/<c>limit</c> - the problem is that nothing made the cheap path the obvious one.
/// </para>
/// <para>
/// These options control two read-path-only guardrails. Neither changes what the tool can express:
/// no new arguments, no path or permission changes.
/// </para>
/// </remarks>
public sealed class ReadToolOptions
{
    /// <summary>
    /// Default size-notice threshold in UTF-8 bytes (20 KiB). Chosen to match the ">20KB" bucket the
    /// #2689 measurement used to isolate the 636 oversized reads carrying 42% of all read tokens.
    /// </summary>
    public const int DefaultLargeReadThresholdBytes = 20 * 1024;

    /// <summary>
    /// Threshold in UTF-8 bytes above which a read result carries an explicit size indicator naming
    /// <c>offset</c> and <c>limit</c> as the narrowing controls. A value of zero or less disables
    /// the indicator, matching the <c>ToolResultPersistence.MaxBytes</c> convention.
    /// </summary>
    public int LargeReadThresholdBytes { get; init; } = DefaultLargeReadThresholdBytes;

    /// <summary>
    /// Whether an identical, byte-for-byte unchanged re-read of the same slice within the same
    /// session returns a short "unchanged" marker instead of the full body. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Elision is only ever applied when the freshly-read content hashes to the same token as the
    /// previous read of the same slice, so a <em>changed</em> file can never take the cheap path.
    /// The file is read from disk on every call regardless; only the payload returned to the model
    /// is elided. Disk reads are free relative to context tokens, so this buys safety for nothing.
    /// </remarks>
    public bool ElideUnchangedRereads { get; init; } = true;
}
