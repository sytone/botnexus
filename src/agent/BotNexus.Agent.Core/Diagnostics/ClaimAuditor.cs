using System.Text.RegularExpressions;

namespace BotNexus.Agent.Core.Diagnostics;

/// <summary>
/// Post-turn claim auditor (#1600, control #1 of #1551 — "highest leverage").
/// </summary>
/// <remarks>
/// <para>
/// Scans an agent's final user-facing message for <em>artifact-shaped claims</em>
/// (a GitHub issue was filed, a PR opened, a file written, something sent/deployed,
/// an audit "verified") and cross-checks each claim against the set of tools that were
/// actually invoked during the run. A claim with no backing tool call is reported as an
/// <see cref="UnbackedClaim"/>.
/// </para>
/// <para>
/// This inverts the trust model that failed in the reproducing incident (#1551): every
/// existing anti-fabrication guardrail lived in the prompt — the exact layer that was
/// ignored when the agent narrated "filed issue #N" and "ran a security audit" with no
/// tool calls emitted. The auditor <em>verifies</em> instead of trusting, so it is a
/// durable control rather than a sixth soft instruction.
/// </para>
/// <para>
/// The detection is deliberately conservative to keep false positives near zero: a claim
/// is flagged only when <em>none</em> of its plausible backing tools
/// (<see cref="ClaimAuditOptions.BackingTools"/>) appear among the invoked tools. Because
/// GitHub work in BotNexus runs through the generic <c>shell</c>/<c>exec</c> tools, a
/// genuine <c>gh issue create</c> is never flagged; a pure narration with zero side-effect
/// tools always is.
/// </para>
/// </remarks>
public static class ClaimAuditor
{
    private const RegexOptions Flags =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // Match windows are bounded to a few words so an artifact verb and its noun must be
    // adjacent — "I filed issue #1234" matches; "issue #1234 is unrelated" does not.
    // Past/perfect tense is required so future-tense intent ("I will file #1234") is excluded.

    // "filed issue #N", "opened issue #1234", "created issue 1234", "issue #N has been filed",
    // "I've filed #1234". Requires a creation verb adjacent to an issue reference.
    private static readonly Regex IssueFiled = new(
        @"(?<!\bwill\s)(?<!\bgoing\sto\s)\b(filed|created|opened|raised|logged|submitted)\b[^.\n]{0,40}?\bissue\b[^.\n]{0,12}?#?\d+" +
        @"|\bissue\b[^.\n]{0,12}?#?\d+[^.\n]{0,40}?\b(has\sbeen|was|is|been)\s(filed|created|opened|raised|logged|submitted)\b" +
        @"|(?<!\bwill\s)(?<!\bgoing\sto\s)\b(filed|created|opened|raised|logged|submitted)\b\s+#\d+",
        Flags);

    // "opened PR #42", "created a pull request", "PR #99 merged", "merged the PR".
    private static readonly Regex PullRequest = new(
        @"(?<!\bwill\s)(?<!\bgoing\sto\s)\b(opened|created|raised|submitted|merged)\b[^.\n]{0,40}?\b(pr|pull\srequest)\b" +
        @"|\b(pr|pull\srequest)\b[^.\n]{0,12}?#?\d+[^.\n]{0,40}?\b(has\sbeen|was|is|been)?\s?(opened|created|raised|submitted|merged)\b" +
        @"|(?<!\bwill\s)(?<!\bgoing\sto\s)\b(opened|created|merged)\b\s+(pr\s+)?#\d+",
        Flags);

    // A concrete GitHub issue/PR URL is itself an artifact claim ("here is the PR: .../pull/99").
    private static readonly Regex ArtifactUrl = new(
        @"https?://[^\s)]+/(issues|pull)/\d+",
        Flags);

    // "wrote `path`", "created the file X", "saved src/Foo.cs". Requires a write verb.
    private static readonly Regex FileWritten = new(
        @"(?<!\bwill\s)(?<!\bgoing\sto\s)\b(wrote|created|saved|added|generated)\b[^.\n]{0,30}?(`[^`\n]+`|\bfile\b|[\w./\\-]+\.\w{1,5})",
        Flags);

    // "sent the summary", "emailed Jon", "posted to the channel", "delivered the report".
    private static readonly Regex Sent = new(
        @"(?<!\bwill\s)(?<!\bgoing\sto\s)\b(sent|emailed|posted|delivered|messaged|notified)\b",
        Flags);

    // "deployed to production", "released v1.2", "published the package".
    private static readonly Regex Deployed = new(
        @"(?<!\bwill\s)(?<!\bgoing\sto\s)\b(deployed|released|published|shipped)\b",
        Flags);

    // "ran the security audit and verified all checks passed" / "verified: ... pass".
    // Requires an audit/verification verb adjacent to a pass/complete assertion to avoid
    // flagging the common, benign "I verified the code looks correct".
    private static readonly Regex VerifiedAudit = new(
        @"\b(ran|performed|completed)\b[^.\n]{0,40}?\b(audit|scan|checks?)\b" +
        @"|\bverified\b[^.\n]{0,40}?\b(all|every)\b[^.\n]{0,20}?\b(pass|passed|passing|green|complete)\b" +
        @"|\b(audit|scan)\b[^.\n]{0,20}?\b(passed|complete|clean|green)\b",
        Flags);

    private static readonly (ClaimCategory Category, Regex Pattern)[] Patterns =
    [
        (ClaimCategory.IssueFiled, IssueFiled),
        (ClaimCategory.PullRequest, PullRequest),
        (ClaimCategory.ArtifactUrl, ArtifactUrl),
        (ClaimCategory.FileWritten, FileWritten),
        (ClaimCategory.Sent, Sent),
        (ClaimCategory.Deployed, Deployed),
        (ClaimCategory.VerifiedAudit, VerifiedAudit),
    ];

    /// <summary>
    /// Audits a final assistant message for artifact-shaped claims that lack a backing
    /// tool call.
    /// </summary>
    /// <param name="finalMessageText">
    /// The text of the agent's final user-facing message (the message produced on a turn
    /// that emitted no further tool calls).
    /// </param>
    /// <param name="toolsInvoked">
    /// The set of tool names invoked at any point during the run. Comparison is
    /// case-insensitive (the set should be created with an ordinal-ignore-case comparer,
    /// but the auditor normalises regardless).
    /// </param>
    /// <param name="options">The auditor configuration.</param>
    /// <returns>
    /// A <see cref="ClaimAuditResult"/> listing unbacked claims (empty when the auditor is
    /// disabled, the message is empty, or every detected claim is backed).
    /// </returns>
    public static ClaimAuditResult Audit(
        string? finalMessageText,
        IReadOnlySet<string> toolsInvoked,
        ClaimAuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(toolsInvoked);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled || string.IsNullOrWhiteSpace(finalMessageText))
        {
            return ClaimAuditResult.Empty(options.Mode);
        }

        // Normalise the invoked-tool set so callers do not have to use a specific comparer.
        // If it is already an ordinal-ignore-case HashSet, reuse it; otherwise rebuild one.
        var invoked = toolsInvoked is HashSet<string> existing
            && ReferenceEquals(existing.Comparer, StringComparer.OrdinalIgnoreCase)
                ? toolsInvoked
                : new HashSet<string>(toolsInvoked, StringComparer.OrdinalIgnoreCase);

        List<UnbackedClaim>? unbacked = null;

        foreach (var (category, pattern) in Patterns)
        {
            var match = pattern.Match(finalMessageText);
            if (!match.Success)
            {
                continue;
            }

            if (IsBacked(category, invoked, options))
            {
                continue;
            }

            (unbacked ??= []).Add(new UnbackedClaim(category, BuildSnippet(finalMessageText, match)));
        }

        return unbacked is null
            ? ClaimAuditResult.Empty(options.Mode)
            : new ClaimAuditResult(unbacked, options.Mode);
    }

    private static bool IsBacked(
        ClaimCategory category,
        IReadOnlySet<string> invoked,
        ClaimAuditOptions options)
    {
        if (invoked.Count == 0)
        {
            return false;
        }

        if (!options.BackingTools.TryGetValue(category, out var backing) || backing.Count == 0)
        {
            // No backing-tool set configured for this category => cannot prove it unbacked;
            // err on the side of not flagging (avoid false positives).
            return true;
        }

        foreach (var tool in invoked)
        {
            if (backing.Contains(tool))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildSnippet(string text, Match match)
    {
        const int pad = 24;
        var start = Math.Max(0, match.Index - pad);
        var end = Math.Min(text.Length, match.Index + match.Length + pad);
        var snippet = text.Substring(start, end - start).Replace('\n', ' ').Replace('\r', ' ').Trim();
        return snippet.Length > 160 ? snippet[..160] : snippet;
    }
}
