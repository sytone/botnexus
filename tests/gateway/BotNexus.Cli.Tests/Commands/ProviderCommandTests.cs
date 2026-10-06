using System.Text.Json;
using System.Text.RegularExpressions;
using BotNexus.Agent.Providers.Copilot;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Wizard;
using Spectre.Console;

namespace BotNexus.Cli.Tests.Commands;

[Collection("AnsiConsole")]
public partial class ProviderCommandTests : IDisposable
{
    private readonly IAnsiConsole _originalConsole;
    private readonly StringWriter _output = new();

    public ProviderCommandTests()
    {
        // Redirect the static AnsiConsole to a per-test StringWriter so that
        // production code calling AnsiConsole.MarkupLine does not race with
        // other test classes that may dispose the shared writer.
        _originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(_output),
            Ansi = AnsiSupport.No,
            Interactive = InteractionSupport.No
        });
        AnsiConsole.Console.Profile.Width = 300;
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
    public async Task SaveAuthEntry_NamedInstance_PreservesOtherCredentialEntries()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "botnexus-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var authPath = Path.Combine(tempDir, "auth.json");
            await File.WriteAllTextAsync(authPath, """
                {
                  "github-copilot": {
                    "type": "oauth",
                    "refresh": "default-refresh",
                    "access": "default-access",
                    "expires": 1700000000000,
                    "endpoint": "https://api.individual.githubcopilot.com"
                  }
                }
                """);

            ProviderCommand.SaveAuthEntry(
                "copilot-work",
                new OAuthCredentials("work-access", "work-refresh", 1800000000, "https://api.enterprise.githubcopilot.com"),
                tempDir);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(authPath));
            var entries = document.RootElement;
            entries.GetProperty("github-copilot").GetProperty("access").GetString().ShouldBe("default-access");
            entries.GetProperty("copilot-work").GetProperty("access").GetString().ShouldBe("work-access");
            entries.GetProperty("copilot-work").GetProperty("refresh").GetString().ShouldBe("work-refresh");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ExecuteTestAsync_HealthyLiveProvider_UsesGatewayRegistryAndCredentialHealthPath()
    {
        using var server = new MockHttpServer();
        server.SetResponse("/api/providers/new-instance/health", System.Net.HttpStatusCode.OK,
            """{"providerId":"new-instance","status":"healthy","latencyMs":3,"checkedAt":"2026-10-01T00:00:00Z","models":1,"hasCredentials":true,"error":null}""");

        var exit = await ProviderCommand.ExecuteTestAsync(
            server.BaseUrl, "new-instance", CancellationToken.None);

        exit.ShouldBe(0);
        var output = NormalizeOutput(_output.ToString());
        output.ShouldContain("Provider new-instance is active and validated by the running gateway");
        output.ShouldContain("Models: 1");
        output.ShouldContain("Credentials: resolved");
    }

    [Fact]
    public async Task ExecuteTestAsync_UnknownLiveProvider_ReturnsPreciseRegistryRemediation()
    {
        using var server = new MockHttpServer();
        server.SetResponse("/api/providers/new-instance/health", System.Net.HttpStatusCode.NotFound,
            "\"Provider 'new-instance' not found.\"");

        var exit = await ProviderCommand.ExecuteTestAsync(
            server.BaseUrl, "new-instance", CancellationToken.None);

        exit.ShouldBe(1);
        var output = NormalizeOutput(_output.ToString());
        output.ShouldContain("Provider new-instance is not ready in the running gateway");
        output.ShouldContain("absent from the live model registry");
    }

    [Fact]
    public async Task ExecuteTestAsync_ActivationFailure_ReturnsPreciseReconcilerFailure()
    {
        using var server = new MockHttpServer();
        server.SetResponse("/api/providers/new-instance/health", System.Net.HttpStatusCode.ServiceUnavailable,
            """{"providerId":"new-instance","status":"activation_failed","latencyMs":0,"checkedAt":"2026-10-01T00:00:00Z","models":0,"hasCredentials":false,"error":"Provider 'new-instance' requires a base URL for openai-completions."}""");

        var exit = await ProviderCommand.ExecuteTestAsync(
            server.BaseUrl, "new-instance", CancellationToken.None);

        exit.ShouldBe(1);
        var output = NormalizeOutput(_output.ToString());
        output.ShouldContain("Provider new-instance is not ready in the running gateway");
        output.ShouldContain("requires a base URL for openai-completions");
        output.ShouldNotContain("absent from the live model registry");
    }

    [Fact]
    public async Task ExecuteTestAsync_UnhealthyLiveProvider_ReturnsFailureWithGatewayRemediation()
    {
        using var server = new MockHttpServer();
        server.SetResponse("/api/providers/new-instance/health", System.Net.HttpStatusCode.ServiceUnavailable,
            """{"providerId":"new-instance","status":"unhealthy","latencyMs":2,"checkedAt":"2026-10-01T00:00:00Z","models":0,"hasCredentials":false,"error":"No models registered for this provider."}""");

        var exit = await ProviderCommand.ExecuteTestAsync(
            server.BaseUrl, "new-instance", CancellationToken.None);

        exit.ShouldBe(1);
        var output = NormalizeOutput(_output.ToString());
        output.ShouldContain("Provider new-instance is not ready in the running gateway");
        output.ShouldContain("No models registered for this provider");
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
    public async Task ExecuteAddAsync_refuses_disabling_assigned_provider_without_mutation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "botnexus-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var configPath = Path.Combine(tempDir, "config.json");
            var original = """
                {
                  "providers": {
                    "copilot-work": { "type": "github-copilot", "enabled": true, "defaultModel": "original" },
                    "github-copilot": { "type": "github-copilot", "enabled": true }
                  },
                  "agents": {
                    "quill": { "provider": "COPILOT-WORK", "model": "original" },
                    "aurum": { "provider": "copilot-work", "model": "original" },
                    "nova": { "provider": "github-copilot", "model": "original" }
                  },
                  "extensionState": { "keep": "untouched" }
                }
                """;
            await File.WriteAllTextAsync(configPath, original);

            var exit = await new ProviderCommand().ExecuteAddAsync(
                configPath, "copilot-work", api: null, apiKey: null, baseUrl: null,
                defaultModel: "changed", models: Array.Empty<string>(), enabled: false,
                verbose: false, CancellationToken.None);

            exit.ShouldBe(1);
            var output = _output.ToString();
            output.ShouldContain("2 agent(s)");
            output.ShouldContain("aurum");
            output.ShouldContain("quill");
            output.ShouldContain("Reassign");
            (await File.ReadAllTextAsync(configPath)).ShouldBe(original);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ExecuteAddAsync_disables_unassigned_provider_preserving_other_sections()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "botnexus-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var configPath = Path.Combine(tempDir, "config.json");
            await File.WriteAllTextAsync(configPath, """
                {
                  "providers": {
                    "copilot-work": { "type": "github-copilot", "enabled": true, "defaultModel": "original" },
                    "github-copilot": { "type": "github-copilot", "enabled": true }
                  },
                  "agents": { "nova": { "provider": "github-copilot", "model": "original" } },
                  "extensionState": { "keep": "untouched" }
                }
                """);

            var exit = await new ProviderCommand().ExecuteAddAsync(
                configPath, "copilot-work", api: null, apiKey: null, baseUrl: null,
                defaultModel: "updated", models: Array.Empty<string>(), enabled: false,
                verbose: false, CancellationToken.None);

            exit.ShouldBe(0);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            doc.RootElement.GetProperty("providers").GetProperty("copilot-work")
                .GetProperty("enabled").GetBoolean().ShouldBeFalse();
            doc.RootElement.GetProperty("providers").GetProperty("copilot-work")
                .GetProperty("defaultModel").GetString().ShouldBe("updated");
            doc.RootElement.GetProperty("agents").GetProperty("nova")
                .GetProperty("provider").GetString().ShouldBe("github-copilot");
            doc.RootElement.GetProperty("extensionState").GetProperty("keep")
                .GetString().ShouldBe("untouched");
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
    public async Task ExecuteRemoveAsync_refuses_provider_assigned_to_agents_without_mutation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "botnexus-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var configPath = Path.Combine(tempDir, "config.json");
            await File.WriteAllTextAsync(configPath, """
                {
                  "providers": {
                    "copilot-work": {
                      "type": "github-copilot",
                      "enabled": true
                    }
                  },
                  "agents": {
                    "aurum": {
                      "provider": "COPILOT-WORK",
                      "model": "gpt-5.6"
                    },
                    "quill": {
                      "provider": "copilot-work",
                      "model": "claude-sonnet-4.6"
                    },
                    "nova": {
                      "provider": "github-copilot",
                      "model": "gpt-5.6"
                    }
                  }
                }
                """);

            var exit = await new ProviderCommand().ExecuteRemoveAsync(
                configPath, "copilot-work", verbose: false, CancellationToken.None);

            exit.ShouldBe(1);
            var json = await File.ReadAllTextAsync(configPath);
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("providers").TryGetProperty("copilot-work", out _).ShouldBeTrue();
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

    private static string NormalizeOutput(string value)
        => AnsiEscapeSequence().Replace(value, string.Empty);

    [GeneratedRegex("\\x1B\\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscapeSequence();

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
