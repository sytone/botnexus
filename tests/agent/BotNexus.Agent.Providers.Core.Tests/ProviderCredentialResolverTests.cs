using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Core.Tests;

/// <summary>
/// Covers issue #2807: an ambient environment credential must never be admitted behind a
/// declared provider credential, and the ambient path must be observable when it does fire.
/// Each test maps to a numbered acceptance clause on the issue.
/// </summary>
public class ProviderCredentialResolverTests
{
    /// <summary>
    /// Sets the given environment variables for the duration of the action, then restores
    /// their prior values so tests do not leak process-wide environment state.
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
            ProviderCredentialResolver.ResetAmbientWarningsForTesting();
            action();
        }
        finally
        {
            foreach (var (key, value) in prior)
                Environment.SetEnvironmentVariable(key, value);
            ProviderCredentialResolver.ResetAmbientWarningsForTesting();
        }
    }

    /// <summary>Captures warning-level log records so ambient-admission logging can be asserted.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Records.Add((logLevel, formatter(state, exception)));
    }

    // Clause 2: a declared key wins over an environment variable set for the same provider.
    [Fact]
    public void Resolve_DeclaredKeyAndAmbientSet_UsesDeclaredValue()
    {
        WithEnv(new() { ["OPENAI_API_KEY"] = "ambient-value" }, () =>
        {
            var resolved = ProviderCredentialResolver.Resolve("openai", "declared-value");

            Assert.Equal("declared-value", resolved.Value);
            Assert.Equal(CredentialSource.Declared, resolved.Source);
        });
    }

    // Clause 3: a declared-but-blank key must NOT fall through to the ambient value.
    [Fact]
    public void Resolve_DeclaredKeyBlank_DoesNotFallBackToAmbient()
    {
        WithEnv(new() { ["OPENAI_API_KEY"] = "ambient-value" }, () =>
        {
            var resolved = ProviderCredentialResolver.Resolve("openai", "   ");

            Assert.NotEqual("ambient-value", resolved.Value);
            Assert.Equal(CredentialSource.Declared, resolved.Source);
            Assert.False(resolved.HasValue);
        });
    }

    // Clause 3 (empty-string variant): declaring "" is still a declaration, not an absence.
    [Fact]
    public void Resolve_DeclaredKeyEmptyString_DoesNotFallBackToAmbient()
    {
        WithEnv(new() { ["OPENAI_API_KEY"] = "ambient-value" }, () =>
        {
            var resolved = ProviderCredentialResolver.Resolve("openai", string.Empty);

            Assert.NotEqual("ambient-value", resolved.Value);
            Assert.Equal(CredentialSource.Declared, resolved.Source);
        });
    }

    // Clause 4: nothing declared + env var set => ambient IS used.
    [Fact]
    public void Resolve_NothingDeclaredAndAmbientSet_UsesAmbientValue()
    {
        WithEnv(new() { ["OPENAI_API_KEY"] = "ambient-value" }, () =>
        {
            var resolved = ProviderCredentialResolver.Resolve("openai", null);

            Assert.Equal("ambient-value", resolved.Value);
            Assert.Equal(CredentialSource.Ambient, resolved.Source);
        });
    }

    // Clause 4: the ambient transition emits exactly one Warning naming the environment variable.
    [Fact]
    public void Resolve_AmbientAdmitted_WarnsExactlyOnceNamingTheEnvironmentVariable()
    {
        WithEnv(new() { ["OPENAI_API_KEY"] = "ambient-value" }, () =>
        {
            var logger = new CapturingLogger();

            ProviderCredentialResolver.Resolve("openai", null, logger);
            ProviderCredentialResolver.Resolve("openai", null, logger);
            ProviderCredentialResolver.Resolve("openai", null, logger);

            var warnings = logger.Records.Where(r => r.Level == LogLevel.Warning).ToList();
            Assert.Single(warnings);
            Assert.Contains("OPENAI_API_KEY", warnings[0].Message);
        });
    }

    // Clause 4 (negative): the declared path must never emit the ambient warning.
    [Fact]
    public void Resolve_DeclaredKey_DoesNotWarn()
    {
        WithEnv(new() { ["OPENAI_API_KEY"] = "ambient-value" }, () =>
        {
            var logger = new CapturingLogger();

            ProviderCredentialResolver.Resolve("openai", "declared-value", logger);

            Assert.DoesNotContain(logger.Records, r => r.Level == LogLevel.Warning);
        });
    }

    // Clause 5: the github-copilot chain must not admit GH_TOKEN/GITHUB_TOKEN behind a declared key.
    // On this instance GH_TOKEN routinely holds a short-lived GitHub App installation token.
    [Fact]
    public void Resolve_CopilotDeclared_DoesNotAdmitGhTokenOrGithubToken()
    {
        WithEnv(
            new()
            {
                ["COPILOT_GITHUB_TOKEN"] = null,
                ["GH_TOKEN"] = "ghs_installation_token",
                ["GITHUB_TOKEN"] = "ghp_personal_token",
            },
            () =>
            {
                var resolved = ProviderCredentialResolver.Resolve("github-copilot", "declared-copilot-key");

                Assert.Equal("declared-copilot-key", resolved.Value);
                Assert.Equal(CredentialSource.Declared, resolved.Source);
                Assert.NotEqual("ghs_installation_token", resolved.Value);
                Assert.NotEqual("ghp_personal_token", resolved.Value);
            });
    }

    // Clause 5 (blank variant): the highest-severity case — a blank declared Copilot credential
    // must not silently present this instance's GitHub App token to a model provider.
    [Fact]
    public void Resolve_CopilotDeclaredButBlank_DoesNotAdmitGhToken()
    {
        WithEnv(
            new()
            {
                ["COPILOT_GITHUB_TOKEN"] = null,
                ["GH_TOKEN"] = "ghs_installation_token",
                ["GITHUB_TOKEN"] = null,
            },
            () =>
            {
                var resolved = ProviderCredentialResolver.Resolve("github-copilot", "");

                Assert.NotEqual("ghs_installation_token", resolved.Value);
                Assert.False(resolved.HasValue);
                Assert.Equal(CredentialSource.Declared, resolved.Source);
            });
    }

    // Nothing declared and nothing ambient => None, and no warning (there was no transition).
    [Fact]
    public void Resolve_NothingDeclaredAndNothingAmbient_ReturnsNone()
    {
        WithEnv(new() { ["OPENAI_API_KEY"] = null }, () =>
        {
            var logger = new CapturingLogger();

            var resolved = ProviderCredentialResolver.Resolve("openai", null, logger);

            Assert.Equal(CredentialSource.None, resolved.Source);
            Assert.False(resolved.HasValue);
            Assert.DoesNotContain(logger.Records, r => r.Level == LogLevel.Warning);
        });
    }

    // Distinct providers each get their own one-shot warning; dedupe must not suppress across providers.
    [Fact]
    public void Resolve_AmbientAdmittedForTwoProviders_WarnsOncePerProvider()
    {
        WithEnv(
            new() { ["OPENAI_API_KEY"] = "openai-ambient", ["GROQ_API_KEY"] = "groq-ambient" },
            () =>
            {
                var logger = new CapturingLogger();

                ProviderCredentialResolver.Resolve("openai", null, logger);
                ProviderCredentialResolver.Resolve("openai", null, logger);
                ProviderCredentialResolver.Resolve("groq", null, logger);
                ProviderCredentialResolver.Resolve("groq", null, logger);

                var warnings = logger.Records.Where(r => r.Level == LogLevel.Warning).ToList();
                Assert.Equal(2, warnings.Count);
                Assert.Contains(warnings, w => w.Message.Contains("OPENAI_API_KEY"));
                Assert.Contains(warnings, w => w.Message.Contains("GROQ_API_KEY"));
            });
    }
}
