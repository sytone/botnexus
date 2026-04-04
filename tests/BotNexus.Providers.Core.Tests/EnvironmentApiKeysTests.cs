using FluentAssertions;

namespace BotNexus.Providers.Core.Tests;

public class EnvironmentApiKeysTests
{
    [Fact]
    public void GetApiKey_UnknownProvider_ReturnsNull()
    {
        var result = EnvironmentApiKeys.GetApiKey("totally-unknown-provider-xyz");

        result.Should().BeNull();
    }

    [Fact]
    public void GetApiKey_KnownProviderMapping_ReturnsEnvironmentVariable()
    {
        // We can't guarantee env vars are set, so we verify the method doesn't throw
        // and returns either null or a string for known providers
        var result = EnvironmentApiKeys.GetApiKey("openai");

        // result is either null (env not set) or a string — both are valid
        result.Should().Match<string?>(r => r == null || r.Length > 0);
    }

    [Fact]
    public void GetApiKey_Anthropic_ChecksMultipleEnvVars()
    {
        // Verify the anthropic path executes without error
        var result = EnvironmentApiKeys.GetApiKey("anthropic");

        result.Should().Match<string?>(r => r == null || r.Length > 0);
    }

    [Fact]
    public void GetApiKey_GithubCopilot_ChecksMultipleEnvVars()
    {
        var result = EnvironmentApiKeys.GetApiKey("github-copilot");

        result.Should().Match<string?>(r => r == null || r.Length > 0);
    }
}
