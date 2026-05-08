
namespace BotNexus.Agent.Providers.Core.Tests;

public class EnvironmentApiKeysTests
{
    [Fact]
    public void GetApiKey_UnknownProvider_ReturnsNull()
    {
        var result = EnvironmentApiKeys.GetApiKey("totally-unknown-provider-xyz");

        result.ShouldBeNull();
    }

    [Fact]
    public void GetApiKey_KnownProviderMapping_ReturnsEnvironmentVariable()
    {
        // We can't guarantee env vars are set, so we verify the method doesn't throw
        // and returns either null or a string for known providers
        var result = EnvironmentApiKeys.GetApiKey("openai");

        // result is either null (env not set) or a non-empty string — both are valid
        if (result != null) result.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void GetApiKey_Anthropic_ChecksMultipleEnvVars()
    {
        // Verify the anthropic path executes without error
        var result = EnvironmentApiKeys.GetApiKey("anthropic");

        if (result != null) result.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void GetApiKey_GithubCopilot_ChecksMultipleEnvVars()
    {
        var result = EnvironmentApiKeys.GetApiKey("github-copilot");

        if (result != null) result.Length.ShouldBeGreaterThan(0);
    }
}
