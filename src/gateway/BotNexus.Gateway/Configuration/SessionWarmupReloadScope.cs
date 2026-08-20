namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Answers "can this configuration reload affect the warmed session cache?" for
/// <see cref="Sessions.SessionWarmupService"/> (#2728).
/// </summary>
/// <remarks>
/// <para>Session warmup rebuilds an O(agents) view of session summaries. A config write that
/// touches, say, a prompt template or a webhook secret cannot change that view, so paying for the
/// rebuild is pure waste. This predicate lets the consumer skip those.</para>
/// <para><b>It fails open, and the direction of the list is what makes that true.</b> The classifier
/// enumerates sections that are <i>provably unrelated</i> to warmed session state, and skips only
/// when <b>every</b> changed path falls inside that set. Whole-document writes, absent path sets and
/// any path not on the list all return <see langword="true"/>. Adding a new config section therefore
/// costs a missed optimisation, never a stale cache — completeness of the classification is a
/// performance property, never a correctness one.</para>
/// </remarks>
public static class SessionWarmupReloadScope
{
    /// <summary>
    /// Root configuration sections that cannot influence the warmed session view.
    /// </summary>
    /// <remarks>
    /// The warmed view is derived from the agent roster (<c>agents</c>) and gateway settings
    /// (<c>gateway</c>, which carries both <c>sessionWarmup</c> and <c>sessionStore</c>), so neither
    /// appears here. Everything listed is a section whose contents are consumed elsewhere entirely.
    /// <b>Only add a root here when you can point at the code proving warmup never reads it.</b>
    /// </remarks>
    private static readonly string[] UnrelatedRoots =
    [
        "promptTemplates",
        "cron",
        "channels",
        "workspace",
        "featureManagement",
        "FeatureManagement",
        "apiKey",
        "version",
        "$schema"
    ];

    /// <summary>
    /// Returns <see langword="true"/> when the reload may affect warmed session state, and therefore
    /// when the warm worker must run. Returns <see langword="false"/> only when every changed path is
    /// recognised <i>and</i> provably unrelated.
    /// </summary>
    public static bool Affects(ConfigReloadPlan? plan)
    {
        // Fail open: no plan at all is indistinguishable from "we do not know what changed".
        if (plan is null || plan.IsWholeDocument)
            return true;

        foreach (var path in plan.ChangedPaths)
        {
            var segments = ConfigReloadPlan.SplitPath(path);

            // An unparseable path carries no information — treat it as affecting.
            if (segments.Length == 0)
                return true;

            // Not on the provably-unrelated list ⇒ assume it affects us and do the full reload.
            if (!UnrelatedRoots.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
