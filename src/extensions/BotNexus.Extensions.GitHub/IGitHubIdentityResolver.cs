using BotNexus.Domain.Primitives;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// Resolves the acting GitHub identity for an agent from configuration (#2733).
/// </summary>
/// <remarks>
/// Deliberate non-goals: there is no <c>SetIdentity</c>, no <c>CurrentIdentity</c>, and no
/// "switch" operation of any shape. A mutable current-identity concept is exactly what ambient
/// <c>gh auth</c> state provides and exactly what this seam exists to remove — every operation is a
/// pure function of the agent id and the configuration.
/// </remarks>
public interface IGitHubIdentityResolver
{
    /// <summary>
    /// Returns the configured acting identity for <paramref name="agentId"/>, or throws a
    /// <see cref="GitHubCredentialException"/> naming the missing configuration key.
    /// </summary>
    GitHubActingIdentity ResolveIdentity(AgentId agentId);

    /// <summary>
    /// Returns a credential provider bound to the agent's configured identity, or throws a
    /// <see cref="GitHubCredentialException"/> naming the missing configuration key.
    /// </summary>
    IGitHubCredentialProvider ResolveCredentialProvider(AgentId agentId);
}
