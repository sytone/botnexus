namespace BotNexus.Extensions.BrowserTools;

/// <summary>
/// Operator-facing knobs for the browser guard layer (#3030).
/// </summary>
/// <remarks>
/// Deliberately small. Every value here bounds or widens a guard, so each one is a security
/// decision an operator must make explicitly rather than a behaviour the tools may assume.
/// </remarks>
public sealed class BrowserToolsConfig
{
    /// <summary>
    /// Default ceiling on snapshot text handed back to the model.
    /// </summary>
    public const int DefaultSnapshotMaxChars = 20_000;

    /// <summary>
    /// Maximum number of characters of page text returned inline to the model. Text beyond this
    /// is spilled to the agent workspace and referenced by path rather than summarised, so no
    /// attacker-controlled text is ever paraphrased by a model that then trusts the paraphrase.
    /// </summary>
    public int SnapshotMaxChars { get; init; } = DefaultSnapshotMaxChars;

    /// <summary>
    /// Extra hostnames blocked in addition to the shared SSRF policy (exact, case-insensitive).
    /// Passed straight through to <c>SsrfValidator</c>; this type defines no address rules.
    /// </summary>
    public IReadOnlyList<string> AdditionalBlockedHosts { get; init; } = [];
}
