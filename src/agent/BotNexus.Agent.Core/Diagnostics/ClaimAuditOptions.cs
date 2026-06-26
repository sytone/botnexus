namespace BotNexus.Agent.Core.Diagnostics;

/// <summary>
/// The category of artifact-shaped claim detected by the <see cref="ClaimAuditor"/>.
/// Each category maps to a set of tools that could legitimately produce the claimed
/// artifact (see <see cref="ClaimAuditOptions.BackingTools"/>).
/// </summary>
public enum ClaimCategory
{
    /// <summary>A claim that a GitHub issue was filed/created/opened (e.g. "filed issue #1234").</summary>
    IssueFiled,

    /// <summary>A claim that a pull request was created/opened/merged (e.g. "opened PR #42").</summary>
    PullRequest,

    /// <summary>A claim referencing a created issue/PR URL (e.g. ".../pull/99").</summary>
    ArtifactUrl,

    /// <summary>A claim that a file was written/created/saved (e.g. "wrote `src/Foo.cs`").</summary>
    FileWritten,

    /// <summary>A claim that something was sent/emailed/posted/delivered.</summary>
    Sent,

    /// <summary>A claim that something was deployed/released/published.</summary>
    Deployed,

    /// <summary>A claim that a multi-check audit/verification was run and passed.</summary>
    VerifiedAudit,
}

/// <summary>
/// Controls how the post-turn claim auditor reacts when it detects an unbacked
/// artifact claim.
/// </summary>
public enum ClaimAuditMode
{
    /// <summary>
    /// Detection only: emit a structured signal but do not interfere with the turn.
    /// This is the safe default while false-positive rates are being characterised.
    /// </summary>
    Warn,

    /// <summary>
    /// Detection plus a block signal: the auditor marks the turn as one that should be
    /// blocked/flagged hard. Consumers (the gateway) decide how to surface the block.
    /// </summary>
    Block,
}

/// <summary>
/// Configuration for the post-turn claim auditor (#1600, control #1 of #1551).
/// </summary>
/// <remarks>
/// The auditor inverts the trust model: rather than trusting narration, it checks the
/// agent's final-message claims against the tools actually invoked during the run. The
/// <see cref="BackingTools"/> map is intentionally conservative (a claim is only flagged
/// when <em>none</em> of its plausible backing tools ran) to keep false positives near
/// zero — genuine work that runs through <c>shell</c>/<c>exec</c> is never flagged.
/// </remarks>
public sealed record ClaimAuditOptions
{
    /// <summary>Whether the auditor runs at all. Default <see langword="true"/>.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Warn (signal only) or Block (signal + block marker). Default <see cref="ClaimAuditMode.Warn"/>.</summary>
    public ClaimAuditMode Mode { get; init; } = ClaimAuditMode.Warn;

    /// <summary>
    /// Maps each claim category to the set of tool names that could legitimately have
    /// produced the claimed artifact. A detected claim is "unbacked" only when none of
    /// these tools were invoked during the run. Tool-name comparison is
    /// case-insensitive.
    /// </summary>
    public IReadOnlyDictionary<ClaimCategory, IReadOnlySet<string>> BackingTools { get; init; }
        = DefaultBackingTools;

    /// <summary>
    /// Creates the default options: enabled, warn-mode, with the default backing-tool map.
    /// </summary>
    public static ClaimAuditOptions CreateDefault() => new();

    /// <summary>
    /// The default claim-category to backing-tool-set map. GitHub operations in BotNexus
    /// run through the generic <c>shell</c>/<c>exec</c> tools (there is no dedicated
    /// <c>gh issue create</c> tool), so the GitHub-artifact categories accept those plus
    /// any first-class GitHub/web tools.
    /// </summary>
    public static readonly IReadOnlyDictionary<ClaimCategory, IReadOnlySet<string>> DefaultBackingTools =
        new Dictionary<ClaimCategory, IReadOnlySet<string>>
        {
            [ClaimCategory.IssueFiled] = Set("shell", "exec", "github", "web_fetch"),
            [ClaimCategory.PullRequest] = Set("shell", "exec", "github", "web_fetch"),
            [ClaimCategory.ArtifactUrl] = Set("shell", "exec", "github", "web_fetch", "web_search"),
            [ClaimCategory.FileWritten] = Set("write", "edit", "shell", "exec"),
            [ClaimCategory.Sent] = Set("shell", "exec", "teams", "mail", "m365-communication", "sessions", "agent_converse"),
            [ClaimCategory.Deployed] = Set("shell", "exec", "github"),
            [ClaimCategory.VerifiedAudit] = Set("shell", "exec", "github", "read", "grep", "spawn_subagent"),
        };

    private static IReadOnlySet<string> Set(params string[] values)
        => new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A single artifact-shaped claim detected in a final assistant message that has no
/// backing tool call among the tools invoked during the run.
/// </summary>
/// <param name="Category">The kind of claim detected.</param>
/// <param name="Snippet">A short excerpt of the matched text, for diagnostics.</param>
public sealed record UnbackedClaim(ClaimCategory Category, string Snippet);

/// <summary>
/// The outcome of a <see cref="ClaimAuditor.Audit"/> pass.
/// </summary>
/// <param name="UnbackedClaims">Claims with no backing tool call. Empty when nothing was flagged.</param>
/// <param name="Mode">The mode the audit ran in.</param>
public sealed record ClaimAuditResult(IReadOnlyList<UnbackedClaim> UnbackedClaims, ClaimAuditMode Mode)
{
    /// <summary>An empty result (nothing flagged) in the given mode.</summary>
    public static ClaimAuditResult Empty(ClaimAuditMode mode) => new([], mode);

    /// <summary>True when at least one unbacked claim was detected.</summary>
    public bool HasUnbackedClaims => UnbackedClaims.Count > 0;

    /// <summary>
    /// True when the auditor recommends blocking the turn: an unbacked claim was
    /// detected <em>and</em> the auditor is configured in <see cref="ClaimAuditMode.Block"/>.
    /// </summary>
    public bool ShouldBlock => HasUnbackedClaims && Mode == ClaimAuditMode.Block;
}
