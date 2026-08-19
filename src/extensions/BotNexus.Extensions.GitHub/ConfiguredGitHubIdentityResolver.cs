using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// Resolves the acting GitHub identity for an agent from configuration, and hands back a credential
/// provider bound to that identity alone (#2733).
/// </summary>
/// <remarks>
/// <para><b>The whole point: identity is a lookup, not a mutation.</b> Ambient <c>gh auth</c> state
/// is process-global, so one agent switching accounts silently re-authors another agent's writes.
/// Here each agent id maps, through configuration, to a named profile; each profile gets its OWN
/// <see cref="CachedGitHubCredentialProvider"/> over its OWN token source. Two agents in one process
/// therefore hold two independent token caches and cannot contaminate one another — there is no
/// shared mutable identity to contaminate.</para>
/// <para><b>Fail closed, and name the configuration key.</b> Every rejection path throws a
/// <see cref="GitHubCredentialException"/> whose message contains the fully qualified configuration
/// key that is missing or incomplete. Falling back to a default identity would be worse than
/// failing: the call would succeed under the WRONG authorship, which is unrecoverable after the
/// fact, whereas a named-key failure is fixed in one edit.</para>
/// <para><b>Nothing here reads ambient CLI state.</b> No <c>gh</c> invocation, no
/// <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> environment read. The architecture fence pins that statically.</para>
/// </remarks>
public sealed class ConfiguredGitHubIdentityResolver : IGitHubIdentityResolver
{
    private readonly GitHubCredentialOptions _options;
    private readonly Func<GitHubActingIdentity, IGitHubCredentialProvider> _providerFactory;
    private readonly ConcurrentDictionary<string, IGitHubCredentialProvider> _providers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a resolver over platform options and a per-identity provider factory.</summary>
    public ConfiguredGitHubIdentityResolver(
        GitHubCredentialOptions options,
        Func<GitHubActingIdentity, IGitHubCredentialProvider> providerFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
    }

    /// <inheritdoc />
    public GitHubActingIdentity ResolveIdentity(AgentId agentId)
    {
        var agent = agentId.Value;

        if (!_options.AgentIdentities.TryGetValue(agent, out var profileName)
            || string.IsNullOrWhiteSpace(profileName))
        {
            throw new GitHubCredentialException(
                $"No GitHub acting identity is configured for agent '{agent}'. "
                + $"Set the configuration key '{AgentKey(agent)}' to the name of a profile "
                + $"declared under '{IdentitiesSection}'.");
        }

        if (!_options.Identities.TryGetValue(profileName, out var profile) || profile is null)
        {
            throw new GitHubCredentialException(
                $"Agent '{agent}' names GitHub identity profile '{profileName}', but no such profile "
                + $"is configured. Add the configuration key '{ProfileKey(profileName)}'.");
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.AppId))
            missing.Add($"{ProfileKey(profileName)}:appId");
        if (string.IsNullOrWhiteSpace(profile.InstallationId))
            missing.Add($"{ProfileKey(profileName)}:installationId");
        if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
            missing.Add($"{ProfileKey(profileName)}:privateKeyPath");

        if (missing.Count > 0)
        {
            // The keys, never the values: a message that echoed a resolved path or app id would make
            // the error itself a disclosure surface.
            throw new GitHubCredentialException(
                $"GitHub identity profile '{profileName}' (used by agent '{agent}') is incomplete. "
                + $"Missing configuration key(s): {string.Join(", ", missing)}.");
        }

        return new GitHubActingIdentity(profileName, profile.AppId!, profile.InstallationId!, profile.PrivateKeyPath!);
    }

    /// <inheritdoc />
    public IGitHubCredentialProvider ResolveCredentialProvider(AgentId agentId)
    {
        var identity = ResolveIdentity(agentId);

        // Keyed by PROFILE, not by agent: two agents configured to the same identity legitimately
        // share one token cache, while two agents on different profiles can never share one.
        return _providers.GetOrAdd(identity.Name, _ => _providerFactory(identity));
    }

    private const string IdentitiesSection = GitHubCredentialOptions.SectionName + ":identities";

    private static string ProfileKey(string profileName) => $"{IdentitiesSection}:{profileName}";

    private static string AgentKey(string agent) =>
        $"{GitHubCredentialOptions.SectionName}:agentIdentities:{agent}";
}
