namespace BotNexus.Integration.ProviderTests;

/// <summary>
/// Skip guard for integration tests that require a GITHUB_TOKEN.
/// Tests are gracefully skipped when the token is not present (local dev runs).
/// </summary>
public class RequiresGitHubTokenFactAttribute : FactAttribute
{
    public RequiresGitHubTokenFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_TOKEN")))
        {
            Skip = "GITHUB_TOKEN environment variable not set. Skipping integration test.";
        }
    }
}
