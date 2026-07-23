using System.Text.Json;
using BotNexus.Agent.Providers.Copilot;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Wizard;
using Spectre.Console;

namespace BotNexus.Cli.Tests.Commands;

[Collection("AnsiConsole")]
public class ProviderCommandTests : IDisposable
{
    private readonly IAnsiConsole _originalConsole;

    public ProviderCommandTests()
    {
        // Redirect the static AnsiConsole to a per-test StringWriter so that
        // production code calling AnsiConsole.MarkupLine does not race with
        // other test classes that may dispose the shared writer.
        _originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(new StringWriter()),
            Interactive = InteractionSupport.No
        });
    }

    public void Dispose()
    {
        AnsiConsole.Console = _originalConsole;
    }

    [Fact]
    public void AuthFileEntry_serializes_in_GatewayAuthManager_compatible_format()
    {
        var entry = new ProviderCommand.AuthFileEntry
        {
            Type = "oauth",
            Refresh = "ghu_refresh_token",
            Access = "tid=copilot_session_token",
            Expires = 1700000000000,
            Endpoint = "https://api.individual.githubcopilot.com"
        };

        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        // Verify the JSON uses the exact property names GatewayAuthManager expects
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("type").GetString().ShouldBe("oauth");
        root.GetProperty("refresh").GetString().ShouldBe("ghu_refresh_token");
        root.GetProperty("access").GetString().ShouldBe("tid=copilot_session_token");
        root.GetProperty("expires").GetInt64().ShouldBe(1700000000000);
        root.GetProperty("endpoint").GetString().ShouldBe("https://api.individual.githubcopilot.com");
    }

    [Fact]
    public void AuthFileEntry_roundtrips_through_dictionary_serialization()
    {
        var entries = new Dictionary<string, ProviderCommand.AuthFileEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["github-copilot"] = new()
            {
                Type = "oauth",
                Refresh = "refresh123",
                Access = "access456",
                Expires = 1700000000000,
                Endpoint = "https://api.githubcopilot.com"
            }
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        var json = JsonSerializer.Serialize(entries, options);
        var deserialized = JsonSerializer.Deserialize<Dictionary<string, ProviderCommand.AuthFileEntry>>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        deserialized.ShouldNotBeNull();
        deserialized.ShouldContainKey("github-copilot");
        deserialized["github-copilot"].Type.ShouldBe("oauth");
        deserialized["github-copilot"].Refresh.ShouldBe("refresh123");
        deserialized["github-copilot"].Access.ShouldBe("access456");
        deserialized["github-copilot"].Expires.ShouldBe(1700000000000);
        deserialized["github-copilot"].Endpoint.ShouldBe("https://api.githubcopilot.com");
    }

    [Fact]
    public void AuthFileEntry_omits_null_endpoint()
    {
        var entry = new ProviderCommand.AuthFileEntry
        {
            Type = "oauth",
            Refresh = "r",
            Access = "a",
            Expires = 100
        };

        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        json.ShouldNotContain("endpoint");
    }

    [Fact]
    public void AuthFileEntry_expires_stores_milliseconds()
    {
        // CopilotOAuth returns ExpiresAt in seconds; auth.json stores milliseconds
        long expiresAtSeconds = 1700000000;
        var entry = new ProviderCommand.AuthFileEntry
        {
            Expires = expiresAtSeconds * 1000
        };

        entry.Expires.ShouldBe(1700000000000);
    }

    [Fact]
    public async Task ExecuteAddAsync_creates_new_provider_with_all_fields()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "botnexus-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var configPath = Path.Combine(tempDir, "config.json");
            var cmd = new ProviderCommand();

            var exit = await cmd.ExecuteAddAsync(
                configPath,
                name: "integration-mock",
                api: "integration-mock",
                apiKey: "n/a",
                baseUrl: null,
                defaultModel: "integration-mock-echo",
                models: new[] { "integration-mock-echo" },
                enabled: true,
                verbose: false,
                CancellationToken.None);

            exit.ShouldBe(0);
            File.Exists(configPath).ShouldBeTrue();

            var json = await File.ReadAllTextAsync(configPath);
            using var doc = JsonDocument.Parse(json);
            var providers = doc.RootElement.GetProperty("providers");
            var prov = providers.GetProperty("integration-mock");
            prov.GetProperty("enabled").GetBoolean().ShouldBeTrue();
            prov.GetProperty("api").GetString().ShouldBe("integration-mock");
            prov.GetProperty("apiKey").GetString().ShouldBe("n/a");
            prov.GetProperty("defaultModel").GetString().ShouldBe("integration-mock-echo");
            prov.GetProperty("models").EnumerateArray().Select(e => e.GetString()).ShouldContain("integration-mock-echo");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ExecuteAddAsync_updates_existing_provider_preserving_unspecified_fields()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "botnexus-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var configPath = Path.Combine(tempDir, "config.json");
            var cmd = new ProviderCommand();

            await cmd.ExecuteAddAsync(
                configPath, "openai", api: "openai-completions", apiKey: "sk-original",
                baseUrl: "https://example.test", defaultModel: "gpt-x", models: Array.Empty<string>(),
                enabled: true, verbose: false, CancellationToken.None);

            // Update only the default model — other fields should remain.
            var exit = await cmd.ExecuteAddAsync(
                configPath, "openai", api: null, apiKey: null,
                baseUrl: null, defaultModel: "gpt-y", models: Array.Empty<string>(),
                enabled: true, verbose: false, CancellationToken.None);

            exit.ShouldBe(0);
            var json = await File.ReadAllTextAsync(configPath);
            using var doc = JsonDocument.Parse(json);
            var prov = doc.RootElement.GetProperty("providers").GetProperty("openai");
            prov.GetProperty("apiKey").GetString().ShouldBe("sk-original");
            prov.GetProperty("baseUrl").GetString().ShouldBe("https://example.test");
            prov.GetProperty("defaultModel").GetString().ShouldBe("gpt-y");
            prov.GetProperty("api").GetString().ShouldBe("openai-completions");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ExecuteAddAsync_disabled_flag_persists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "botnexus-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var configPath = Path.Combine(tempDir, "config.json");
            var cmd = new ProviderCommand();

            await cmd.ExecuteAddAsync(
                configPath, "foo", api: null, apiKey: "k",
                baseUrl: null, defaultModel: null, models: Array.Empty<string>(),
                enabled: false, verbose: false, CancellationToken.None);

            var json = await File.ReadAllTextAsync(configPath);
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("providers").GetProperty("foo").GetProperty("enabled").GetBoolean().ShouldBeFalse();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ExecuteRemoveAsync_removes_provider_when_present()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "botnexus-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var configPath = Path.Combine(tempDir, "config.json");
            var cmd = new ProviderCommand();
            await cmd.ExecuteAddAsync(configPath, "to-remove", null, "k", null, null, Array.Empty<string>(), true, false, CancellationToken.None);

            var exit = await cmd.ExecuteRemoveAsync(configPath, "to-remove", verbose: false, CancellationToken.None);

            exit.ShouldBe(0);
            var json = await File.ReadAllTextAsync(configPath);
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("providers").TryGetProperty("to-remove", out _).ShouldBeFalse();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ExecuteRemoveAsync_returns_zero_when_provider_missing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "botnexus-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var configPath = Path.Combine(tempDir, "config.json");
            var cmd = new ProviderCommand();
            var exit = await cmd.ExecuteRemoveAsync(configPath, "never-existed", verbose: false, CancellationToken.None);
            exit.ShouldBe(0);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ExecuteAddAsync_ollama_provider_with_baseUrl_and_api()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "botnexus-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var configPath = Path.Combine(tempDir, "config.json");
            var cmd = new ProviderCommand();

            var exit = await cmd.ExecuteAddAsync(
                configPath,
                name: "ollama",
                api: "openai-completions",
                apiKey: "ollama",
                baseUrl: "http://localhost:11434/v1",
                defaultModel: "llama3.2",
                models: Array.Empty<string>(),
                enabled: true,
                verbose: false,
                CancellationToken.None);

            exit.ShouldBe(0);
            var json = await File.ReadAllTextAsync(configPath);
            using var doc = JsonDocument.Parse(json);
            var prov = doc.RootElement.GetProperty("providers").GetProperty("ollama");
            prov.GetProperty("enabled").GetBoolean().ShouldBeTrue();
            prov.GetProperty("apiKey").GetString().ShouldBe("ollama");
            prov.GetProperty("baseUrl").GetString().ShouldBe("http://localhost:11434/v1");
            prov.GetProperty("api").GetString().ShouldBe("openai-completions");
            prov.GetProperty("defaultModel").GetString().ShouldBe("llama3.2");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task OAuthFlowStep_on_success_jumps_to_pick_model_not_ollama_setup()
    {
        // Regression: the OAuth path used to return Continue(), falling through
        // into the Ollama setup step which overwrote baseUrl/api with local
        // Ollama values. It must jump straight to model selection instead.
        var step = new ProviderCommand.OAuthFlowStep(
            (_, _, _) => Task.FromResult<OAuthCredentials?>(
                new OAuthCredentials("access", "refresh", 1700000000)));

        var context = new WizardContext();
        context.Set("provider", "github-copilot");
        context.Set("home", Path.GetTempPath());

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Outcome.ShouldBe(StepOutcome.GoTo);
        result.GoToStep.ShouldBe("pick-model");
    }

    [Fact]
    public async Task OAuthFlowStep_on_failure_aborts()
    {
        var step = new ProviderCommand.OAuthFlowStep(
            (_, _, _) => Task.FromResult<OAuthCredentials?>(null));

        var context = new WizardContext();
        context.Set("provider", "github-copilot");
        context.Set("home", Path.GetTempPath());

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Outcome.ShouldBe(StepOutcome.Abort);
    }
}
