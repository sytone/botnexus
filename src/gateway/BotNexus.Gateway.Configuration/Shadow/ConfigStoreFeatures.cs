namespace BotNexus.Gateway.Configuration.Shadow;

/// <summary>
/// The two independent feature flags governing the configuration store rollout (#2766 AC1, AC2).
///
/// <para>
/// <b>Two flags, never one - the separation is the whole design.</b> A single "use the new store" flag
/// would make migration correctness and cutover the same decision, so the first evidence that the
/// migration is faithful would arrive when the platform already depended on it. Splitting them means
/// shadow mode can run for as long as it takes to build confidence - across restarts, config edits,
/// agent additions, extension installs - while the gateway's behaviour is unchanged and
/// <em>unchangeable</em> by the new code path.
/// </para>
///
/// <para>
/// Three states, in order:
/// <list type="number">
///   <item>Both off - today's behaviour, zero new code paths active. This is the default.</item>
///   <item>Shadow on, authoritative off - the migration runs and is verified every start; JSON remains authoritative.</item>
///   <item>Both on - the store is authoritative; the shadow diff continues as a regression guard.</item>
/// </list>
/// </para>
/// </summary>
public static class ConfigStoreFeatures
{
    /// <summary>
    /// Migrate JSON into the store on start and diff the round-trip against the source. Report only:
    /// this flag can never change which configuration the gateway serves.
    /// </summary>
    public const string ShadowMigration = "ConfigStoreShadowMigration";

    /// <summary>
    /// Read configuration from the store instead of JSON. Only defensible once
    /// <see cref="ShadowMigration"/> has produced clean diffs over a meaningful period, which is why
    /// <see cref="ConfigStoreFeatureGate"/> refuses to start when this is on and shadow is off.
    /// </summary>
    public const string Authoritative = "ConfigStoreAuthoritative";
}

/// <summary>
/// Thrown when the configuration store flags are in a combination that cannot be honoured safely.
/// </summary>
public sealed class ConfigStoreFeatureStateException(string message) : InvalidOperationException(message);

/// <summary>
/// Validates the flag combination at startup (#2766 AC2).
///
/// <para>
/// <b>Why authoritative-without-shadow is refused rather than silently upgraded.</b> Turning the store
/// authoritative without the verification path having run means the platform depends on a migration
/// nothing has ever checked. The two available alternatives are both worse: silently enabling shadow
/// as well would mean the operator's stated intent and the running configuration disagree, and
/// proceeding without verification would put an unverified store in the read path - the precise
/// scenario the two-flag split exists to prevent. Refusing loudly at startup is the only option that
/// leaves the operator informed and the platform honest.
/// </para>
///
/// <para>
/// Note this refusal is deliberately <em>not</em> the same as the shadow path's own failure policy. A
/// shadow migration that throws must never fail startup (AC5) because it is diagnostic; an
/// operator asking for an unverified authoritative store is a configuration error, and configuration
/// errors are exactly what startup validation exists to catch.
/// </para>
/// </summary>
public static class ConfigStoreFeatureGate
{
    /// <summary>
    /// Throws when <paramref name="authoritativeEnabled"/> is set without
    /// <paramref name="shadowEnabled"/>.
    /// </summary>
    /// <param name="shadowEnabled">Whether <see cref="ConfigStoreFeatures.ShadowMigration"/> is on.</param>
    /// <param name="authoritativeEnabled">Whether <see cref="ConfigStoreFeatures.Authoritative"/> is on.</param>
    /// <exception cref="ConfigStoreFeatureStateException">
    /// When the combination is authoritative-without-shadow.
    /// </exception>
    public static void EnsureValid(bool shadowEnabled, bool authoritativeEnabled)
    {
        if (authoritativeEnabled && !shadowEnabled)
        {
            throw new ConfigStoreFeatureStateException(
                $"Feature '{ConfigStoreFeatures.Authoritative}' is enabled but " +
                $"'{ConfigStoreFeatures.ShadowMigration}' is not. The configuration store may not become " +
                "authoritative without the shadow verification path having run: doing so would put a " +
                "migration nothing has ever verified into the configuration read path. Enable " +
                $"'{ConfigStoreFeatures.ShadowMigration}', confirm it reports clean diffs across restarts " +
                "and configuration changes, and only then enable " +
                $"'{ConfigStoreFeatures.Authoritative}'.");
        }
    }
}
