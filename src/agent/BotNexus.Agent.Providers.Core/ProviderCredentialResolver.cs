using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Core;

/// <summary>
/// Where a resolved provider credential came from.
/// </summary>
/// <remarks>
/// The distinction exists because the previous <c>options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(...) ?? ""</c>
/// coalesce collapsed "configuration declared a credential that is blank" and "configuration declared
/// nothing" into the same <c>null</c>, so no call site could tell an authorized fallback from a silent
/// credential substitution. See issue #2807.
/// </remarks>
public enum CredentialSource
{
    /// <summary>No credential was declared and none was found in the environment.</summary>
    None = 0,

    /// <summary>
    /// The credential came from configuration. Also reported when configuration declared a
    /// credential that turned out to be blank — a blank declaration is still a declaration, and
    /// must NOT be widened into an ambient lookup.
    /// </summary>
    Declared = 1,

    /// <summary>
    /// The credential came from the process environment, admitted only because configuration
    /// declared nothing at all for this provider.
    /// </summary>
    Ambient = 2,
}

/// <summary>
/// A provider credential together with the provenance that authorized it.
/// </summary>
/// <param name="Value">
/// The credential, or the empty string when none is available. Never <c>null</c>, so callers keep the
/// existing <c>string.IsNullOrWhiteSpace</c> guard shape without re-introducing a coalesce.
/// </param>
/// <param name="Source">Where <paramref name="Value"/> came from.</param>
public readonly record struct ResolvedProviderCredential(string Value, CredentialSource Source)
{
    /// <summary>Gets a value indicating whether a usable (non-blank) credential was resolved.</summary>
    public bool HasValue => !string.IsNullOrWhiteSpace(Value);
}

/// <summary>
/// The single place a provider credential is resolved from configuration or the process environment.
/// </summary>
/// <remarks>
/// <para>
/// Policy (issue #2807): a credential present only in the process environment must not be used when
/// configuration declared a provider credential. Ambient admission is therefore gated on "nothing was
/// declared" — not on "the declared value happened to be falsy" — and is reported once per provider at
/// Warning level, naming the environment variable responsible.
/// </para>
/// <para>
/// This matters concretely for <c>github-copilot</c>, whose ambient chain reads <c>COPILOT_GITHUB_TOKEN</c>,
/// then <c>GH_TOKEN</c>, then <c>GITHUB_TOKEN</c>. On hosts where <c>GH_TOKEN</c> carries a GitHub App
/// installation token scoped for repository writes, the old coalesce would present that token to a model
/// provider whenever the configured Copilot credential was blank or revoked.
/// </para>
/// </remarks>
public static class ProviderCredentialResolver
{
    // One-shot dedupe so the ambient admission is reported once per provider rather than once per
    // request. Warning-per-request would be thousands of lines a day and would be filtered out.
    private static readonly HashSet<string> WarnedProviders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock WarnedGate = new();

    /// <summary>
    /// Resolves the credential for a provider, recording whether it was declared or ambient.
    /// </summary>
    /// <param name="provider">The provider identifier, e.g. <c>openai</c> or <c>github-copilot</c>.</param>
    /// <param name="declaredApiKey">
    /// The credential declared by configuration or call options. A non-<c>null</c> value — including the
    /// empty string — counts as a declaration and suppresses ambient fallback entirely.
    /// </param>
    /// <param name="logger">Optional logger used to report ambient admission once per provider.</param>
    /// <returns>The resolved credential and its provenance.</returns>
    public static ResolvedProviderCredential Resolve(
        string provider,
        string? declaredApiKey,
        ILogger? logger = null)
    {
        // A declaration wins outright, blank or not. Falling through on blank is precisely the
        // defect: it lets a revoked or cleared credential be replaced by an unrelated ambient one.
        if (declaredApiKey is not null)
            return new ResolvedProviderCredential(declaredApiKey, CredentialSource.Declared);

        var ambient = EnvironmentApiKeys.GetApiKey(provider);
        if (string.IsNullOrWhiteSpace(ambient))
            return new ResolvedProviderCredential(string.Empty, CredentialSource.None);

        WarnAmbientOnce(provider, logger);
        return new ResolvedProviderCredential(ambient, CredentialSource.Ambient);
    }

    /// <summary>
    /// Resets the once-per-provider warning dedupe. Test seam only: the dedupe is process-wide static
    /// state, so tests asserting "exactly one warning" would otherwise be order-dependent.
    /// </summary>
    public static void ResetAmbientWarningsForTesting()
    {
        lock (WarnedGate)
            WarnedProviders.Clear();
    }

    private static void WarnAmbientOnce(string provider, ILogger? logger)
    {
        if (logger is null)
            return;

        lock (WarnedGate)
        {
            if (!WarnedProviders.Add(provider))
                return;
        }

        logger.LogWarning(
            "Provider '{Provider}' has no credential declared in configuration; using the ambient environment "
                + "variable {EnvironmentVariable}. Declare the credential explicitly to avoid depending on "
                + "process environment state.",
            provider,
            EnvironmentApiKeys.DescribeSourceVariable(provider));
    }
}
