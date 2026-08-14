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

    /// <summary>Default agent-browser version. Pinned exactly; never a range (#3029).</summary>
    public const string DefaultPinnedVersion = "0.33.2";

    /// <summary>Default per-command timeout in seconds.</summary>
    public const int DefaultCommandTimeoutSeconds = 60;

    /// <summary>
    /// Explicit path to an agent-browser executable. When set it wins over every other step of
    /// the resolution order, and a wrong value is reported rather than silently fallen back from:
    /// falling through would run a different binary than the one the operator named (#3029).
    /// </summary>
    public string? BinaryPath { get; init; }

    /// <summary>
    /// Exact agent-browser version used for the managed install directory and release lookup.
    /// Pinned deliberately - the tools parse this binary's JSON output, and a floating version
    /// would let that contract change without a single line of this repository changing.
    /// </summary>
    public string PinnedVersion { get; init; } = DefaultPinnedVersion;

    /// <summary>
    /// Whether the resolver may download the pinned release asset. FALSE by default: fetching and
    /// executing a binary is a supply-chain decision, and a default of true would make it on the
    /// operator's behalf the first time any agent touched a browser tool (#3029 AC7).
    /// </summary>
    public bool AutoProvision { get; init; }

    /// <summary>Maximum seconds a single agent-browser command may run before abandonment.</summary>
    public int CommandTimeoutSeconds { get; init; } = DefaultCommandTimeoutSeconds;

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
