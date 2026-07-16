namespace BotNexus.Agent.Providers.Core.Tests;

public class EnvironmentApiKeysTests
{
    /// <summary>
    /// Sets the given environment variables for the duration of the action, then
    /// restores their prior values. Ensures tests do not leak process-wide env state.
    /// </summary>
    private static void WithEnv(Dictionary<string, string?> vars, Action action)
    {
        var prior = new Dictionary<string, string?>();
        foreach (var (key, value) in vars)
        {
            prior[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            action();
        }
        finally
        {
            foreach (var (key, value) in prior)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    [Fact]
    public void GetApiKey_UnknownProvider_ReturnsNull()
    {
        var result = EnvironmentApiKeys.GetApiKey("totally-unknown-provider-xyz");

        result.ShouldBeNull();
    }

    [Fact]
    public void GetApiKey_KnownProviderMapping_ReturnsEnvironmentVariable()
    {
        WithEnv(new() { ["OPENAI_API_KEY"] = "sk-valid-key" }, () =>
        {
            var result = EnvironmentApiKeys.GetApiKey("openai");

            result.ShouldBe("sk-valid-key");
        });
    }

    [Fact]
    public void GetApiKey_SingleVarBlank_ReturnsNull()
    {
        WithEnv(new() { ["OPENAI_API_KEY"] = "" }, () =>
        {
            var result = EnvironmentApiKeys.GetApiKey("openai");

            result.ShouldBeNull();
        });
    }

    [Fact]
    public void GetApiKey_SingleVarWhitespace_ReturnsNull()
    {
        WithEnv(new() { ["OPENAI_API_KEY"] = "   " }, () =>
        {
            var result = EnvironmentApiKeys.GetApiKey("openai");

            result.ShouldBeNull();
        });
    }

    [Fact]
    public void GetApiKey_GithubCopilot_BlankPrimary_FallsThroughToNext()
    {
        WithEnv(new()
        {
            ["COPILOT_GITHUB_TOKEN"] = "",
            ["GH_TOKEN"] = "gh-fallback-token",
            ["GITHUB_TOKEN"] = "github-last",
        }, () =>
        {
            var result = EnvironmentApiKeys.GetApiKey("github-copilot");

            result.ShouldBe("gh-fallback-token");
        });
    }

    [Fact]
    public void GetApiKey_GithubCopilot_WhitespacePrimaryAndSecondary_FallsThroughToLast()
    {
        WithEnv(new()
        {
            ["COPILOT_GITHUB_TOKEN"] = "   ",
            ["GH_TOKEN"] = "",
            ["GITHUB_TOKEN"] = "github-last",
        }, () =>
        {
            var result = EnvironmentApiKeys.GetApiKey("github-copilot");

            result.ShouldBe("github-last");
        });
    }

    [Fact]
    public void GetApiKey_GithubCopilot_AllBlank_ReturnsNull()
    {
        WithEnv(new()
        {
            ["COPILOT_GITHUB_TOKEN"] = "",
            ["GH_TOKEN"] = "   ",
            ["GITHUB_TOKEN"] = "",
        }, () =>
        {
            var result = EnvironmentApiKeys.GetApiKey("github-copilot");

            result.ShouldBeNull();
        });
    }

    [Fact]
    public void GetApiKey_GithubCopilot_ValidPrimary_ReturnsPrimary()
    {
        WithEnv(new()
        {
            ["COPILOT_GITHUB_TOKEN"] = "copilot-primary",
            ["GH_TOKEN"] = "gh-fallback-token",
            ["GITHUB_TOKEN"] = "github-last",
        }, () =>
        {
            var result = EnvironmentApiKeys.GetApiKey("github-copilot");

            result.ShouldBe("copilot-primary");
        });
    }

    [Fact]
    public void GetApiKey_Anthropic_BlankOAuthToken_FallsThroughToApiKey()
    {
        WithEnv(new()
        {
            ["ANTHROPIC_OAUTH_TOKEN"] = "",
            ["ANTHROPIC_API_KEY"] = "anthropic-api-key",
        }, () =>
        {
            var result = EnvironmentApiKeys.GetApiKey("anthropic");

            result.ShouldBe("anthropic-api-key");
        });
    }

    [Fact]
    public void GetApiKey_Anthropic_WhitespaceBoth_ReturnsNull()
    {
        WithEnv(new()
        {
            ["ANTHROPIC_OAUTH_TOKEN"] = "   ",
            ["ANTHROPIC_API_KEY"] = "",
        }, () =>
        {
            var result = EnvironmentApiKeys.GetApiKey("anthropic");

            result.ShouldBeNull();
        });
    }

    [Fact]
    public void GetApiKey_Anthropic_ValidOAuthToken_ReturnsOAuthToken()
    {
        WithEnv(new()
        {
            ["ANTHROPIC_OAUTH_TOKEN"] = "oauth-token",
            ["ANTHROPIC_API_KEY"] = "anthropic-api-key",
        }, () =>
        {
            var result = EnvironmentApiKeys.GetApiKey("anthropic");

            result.ShouldBe("oauth-token");
        });
    }
}
