namespace BotNexus.Extensions.GitHub;

/// <summary>
/// Mints a fresh GitHub App installation token. Implemented by the HTTP-backed source in production
/// and by a fake in tests, so the caching policy can be exercised without a network or a clock wait.
/// </summary>
public interface IGitHubInstallationTokenSource
{
    /// <summary>
    /// Requests a new installation access token from GitHub. Implementations must not log, export, or
    /// otherwise persist the returned value beyond returning it.
    /// </summary>
    Task<GitHubInstallationToken> MintAsync(CancellationToken cancellationToken = default);
}
