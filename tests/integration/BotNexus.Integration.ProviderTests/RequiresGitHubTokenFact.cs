using Xunit;

namespace BotNexus.Integration.ProviderTests;

/// <summary>
/// Skip guard for integration tests that require a GITHUB_TOKEN.
/// Tests are gracefully skipped when the token is not present (local dev runs).
/// Inherits from <see cref="SkippableFactAttribute"/> so tests can also skip
/// at runtime via <c>Skip.If(...)</c> when the external API is degraded.
/// </summary>
public class RequiresGitHubTokenFactAttribute : SkippableFactAttribute
{
    public RequiresGitHubTokenFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_TOKEN")))
        {
            Skip = "GITHUB_TOKEN environment variable not set. Skipping integration test.";
        }
    }
}
