using BotNexus.Domain.Primitives;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// A credential provider bound to one agent id that defers identity resolution to the moment a
/// request is actually authenticated (#2733).
/// </summary>
/// <remarks>
/// <para><b>Why resolution is deferred rather than done at tool-contribution time.</b> Contribution
/// happens during agent handle creation; throwing there would turn a GitHub misconfiguration into an
/// agent that fails to start, with the cause several layers away from the message. Deferring to the
/// authenticate step means the failure lands on the tool call that needed the identity, carrying the
/// configuration key that is missing — which is what AC3 of #2733 asks for.</para>
/// <para>Resolution is repeated per request on purpose: it is a dictionary lookup, and it means a
/// configuration reload takes effect without recycling the agent. The expensive part — the minted
/// installation token — is still cached per identity by the resolver.</para>
/// </remarks>
public sealed class AgentScopedGitHubCredentialProvider : IGitHubCredentialProvider
{
    private readonly IGitHubIdentityResolver _resolver;
    private readonly AgentId _agentId;

    /// <summary>Creates a provider that always acts as <paramref name="agentId"/>'s configured identity.</summary>
    public AgentScopedGitHubCredentialProvider(IGitHubIdentityResolver resolver, AgentId agentId)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _agentId = agentId;
    }

    /// <summary>The agent this provider acts as. Read-only: there is no setter to switch it.</summary>
    public AgentId AgentId => _agentId;

    /// <inheritdoc />
    public Task AuthenticateAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Throws GitHubCredentialException naming the missing configuration key when this agent has
        // no resolvable identity. Failing closed is the point: a fallback would succeed under the
        // wrong authorship, which cannot be undone after the write lands.
        return _resolver.ResolveCredentialProvider(_agentId).AuthenticateAsync(request, cancellationToken);
    }
}
