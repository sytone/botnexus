namespace BotNexus.Gateway.Configuration;

/// <summary>
/// A single declared feature flag: its name, the default applied when it is absent from
/// configuration, and what it does.
/// </summary>
/// <param name="Name">
/// The flag key as it appears under the <c>FeatureManagement</c> section of config.json. This is
/// the exact string handed to <c>IFeatureManager.IsEnabledAsync</c>, so it is case-sensitive by
/// the same rules Microsoft.FeatureManagement applies.
/// </param>
/// <param name="Default">
/// The value applied when the flag is absent from configuration. This is documentation of an
/// existing behaviour, not a new policy: it must match what the evaluating code actually does when
/// the flag cannot be read, or the inventory becomes a second, disagreeing source of truth.
/// </param>
/// <param name="Description">
/// Operator-facing explanation. Surfaced by <c>doctor config</c> when reporting the flag as an
/// unstated decision, so it must say what turning the flag on changes - an operator seeding a value
/// should not have to read source to know what they are enabling.
/// </param>
public sealed record FeatureFlagDefinition(string Name, bool Default, string Description);

/// <summary>
/// The complete inventory of feature flags the platform declares (#2767).
/// <para>
/// Before this type existed the only way to answer "which flags exist?" was to grep for string
/// literals, and <c>GatewayDevOriginEnforcement</c> was declared independently in two files with
/// nothing binding them - a rename in one was silently unobserved by the other. Every consumer now
/// references <see cref="GatewayDevOriginEnforcement"/>, so the name has exactly one definition and
/// the compiler enforces agreement.
/// </para>
/// <para>
/// This inventory is also what makes absence reportable. A flag missing from config.json is
/// indistinguishable from a flag that was deliberately disabled, from one nobody has heard of, and
/// from a typo - all four evaluate identically. <c>doctor config</c> can only call out an unstated
/// decision, or flag an unrecognised key as a probable misspelling, because <see cref="All"/>
/// enumerates what is supposed to be there.
/// </para>
/// <para>
/// Adding a flag means adding it here. That is deliberate: a flag introduced without an entry is
/// invisible to the tooling, which is the exact defect #2767 exists to close.
/// </para>
/// </summary>
public static class FeatureFlags
{
    /// <summary>
    /// The config.json section feature flags live under. PascalCase because this is the section
    /// name Microsoft.FeatureManagement binds, and the name is shared with
    /// <see cref="PlatformConfig.FeatureManagement"/> so the model and the binder cannot drift.
    /// </summary>
    public const string SectionName = "FeatureManagement";

    /// <summary>
    /// Gates the dev-mode browser-Origin guard (#1931), which defends the auto-granted
    /// <c>gateway-dev</c> admin identity against DNS-rebind / CSRF from a malicious web origin.
    /// <para>
    /// Defaults to <b>off</b>, and that is load-bearing rather than incidental: the guard rejects
    /// requests by Origin, so enabling it for an operator who reaches the UI over a LAN hostname,
    /// reverse proxy or overlay network without first allow-listing that origin locks them out of
    /// their own gateway on restart. It is opt-in until the operator has stated their origins.
    /// </para>
    /// </summary>
    public const string GatewayDevOriginEnforcement = "GatewayDevOriginEnforcement";

    /// <summary>
    /// Every declared flag. Ordered by name so <c>doctor config</c> output and any seeded config
    /// block are stable across runs - an unstable order turns a no-op re-run into a spurious diff.
    /// </summary>
    public static readonly IReadOnlyList<FeatureFlagDefinition> All =
    [
        new FeatureFlagDefinition(
            GatewayDevOriginEnforcement,
            Default: false,
            "Enforces the browser Origin header on keyless (dev-mode) gateway requests, protecting "
            + "the auto-granted gateway-dev admin identity from DNS-rebind and CSRF. Off by default: "
            + "enable it only after gateway.cors.allowedOrigins lists every origin you use, or you "
            + "will be locked out of the UI on restart."),
    ];

    /// <summary>
    /// Looks up a declared flag by name, returning null when the name is not in the inventory.
    /// A null result is what <c>doctor config</c> reports as an unrecognised key - a probable typo
    /// or a stale flag left behind by a removed feature.
    /// </summary>
    public static FeatureFlagDefinition? Find(string? name)
        => name is null
            ? null
            : All.FirstOrDefault(flag => string.Equals(flag.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns whether <paramref name="name"/> is a declared flag.</summary>
    public static bool IsDeclared(string? name) => Find(name) is not null;

    /// <summary>
    /// The default applied when <paramref name="name"/> is absent from configuration. Undeclared
    /// names default to <c>false</c>, matching how an unknown flag already evaluates.
    /// </summary>
    public static bool DefaultFor(string? name) => Find(name)?.Default ?? false;
}
