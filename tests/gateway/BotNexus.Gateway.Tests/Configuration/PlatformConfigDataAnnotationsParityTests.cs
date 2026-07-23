using System.Text.Json;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Parity tests for #1613 (config parity PBI 5/6). They prove the DataAnnotations-driven
/// server validation path (<see cref="PlatformConfigLoader.ValidateAnnotated"/>, which runs
/// <see cref="System.ComponentModel.DataAnnotations.Validator.TryValidateObject"/> over the
/// annotated <see cref="PlatformConfig"/> plus its <c>IValidatableObject</c> cross-field escape
/// hatch) accepts and rejects EXACTLY the same configs as the legacy imperative path
/// (<see cref="PlatformConfigLoader.Validate"/>). The goal is "no rule lost": every config that
/// the old path accepted must still be accepted, and every config it rejected must still be
/// rejected, by the new path.
///
/// These tests are written before the implementation is wired (TDD): the new path must reach
/// the same accept/reject verdict as the imperative baseline for representative happy and sad
/// configs. They assert verdict equivalence (any-errors vs no-errors) rather than identical
/// message text, because message text is independently locked down by
/// <see cref="PlatformConfigValidationTests"/>.
/// </summary>
public sealed class PlatformConfigDataAnnotationsParityTests
{
    /// <summary>Representative VALID configs the legacy path accepts (no errors).</summary>
    public static TheoryData<string, string> ValidConfigs() => new()
    {
        {
            "minimal",
            """
            {
              "version": 1,
              "providers": { "copilot": { "apiKey": "test-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "full-gateway-inmemory",
            """
            {
              "gateway": {
                "listenUrl": "http://localhost:5005",
                "defaultAgentId": "assistant",
                "logLevel": "Information",
                "sessionStore": { "type": "InMemory" },
                "cors": { "allowedOrigins": ["https://app.example.test"] },
                "rateLimit": { "enabled": true, "requestsPerMinute": 120, "windowSeconds": 60 }
              },
              "providers": { "copilot": { "apiKey": "test-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "listenUrl-wildcard-plus",
            """
            {
              "gateway": { "listenUrl": "http://+:5000" },
              "providers": { "copilot": { "apiKey": "test-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "sqlite-store-no-connection-string",
            """
            {
              "gateway": { "sessionStore": { "type": "Sqlite" } },
              "providers": { "copilot": { "apiKey": "test-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "integration-mock-filesystem-baseurl",
            """
            {
              "providers": { "integration-mock": { "api": "integration-mock", "baseUrl": "C:\\tmp\\catalog.json" } },
              "agents": { "assistant": { "provider": "integration-mock", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "cron-enabled-with-job",
            """
            {
              "cron": {
                "enabled": true,
                "tickIntervalSeconds": 30,
                "jobs": {
                  "daily": {
                    "schedule": "0 9 * * *",
                    "actionType": "agent-prompt",
                    "agentId": "assistant",
                    "message": "Summarize the day"
                  }
                }
              },
              "providers": { "copilot": { "apiKey": "test-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "crossworld-peers-and-inbound",
            """
            {
              "gateway": {
                "crossWorld": {
                  "peers": { "world-b": { "worldId": "world-b", "endpoint": "https://world-b.example.test", "apiKey": "peer-key", "enabled": true } },
                  "inbound": { "enabled": true, "allowedWorlds": ["world-b"], "apiKeys": { "world-b": "peer-key" } }
                }
              },
              "providers": { "copilot": { "apiKey": "provider-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "memory-prompt-injection-summary",
            """
            {
              "providers": { "copilot": { "apiKey": "provider-key" } },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1",
                  "memory": { "enabled": true, "promptInjection": "summary" }
                }
              }
            }
            """
        },
        {
            "per-key-apikeys-complete",
            """
            {
              "gateway": {
                "apiKeys": {
                  "tenant-a": {
                    "apiKey": "secret-a",
                    "tenantId": "tenant-a",
                    "permissions": ["chat:send", "sessions:read"]
                  }
                }
              },
              "providers": { "copilot": { "apiKey": "provider-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
    };

    /// <summary>Representative INVALID configs the legacy path rejects (at least one error).</summary>
    public static TheoryData<string, string> InvalidConfigs() => new()
    {
        {
            "invalid-session-store-type",
            """
            {
              "gateway": { "sessionStore": { "type": "Redis" } },
              "providers": { "copilot": { "apiKey": "test-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "invalid-listenurl-format",
            """
            {
              "gateway": { "listenUrl": "not-a-url" },
              "providers": { "copilot": { "apiKey": "test-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "invalid-listenurl-scheme",
            """
            {
              "gateway": { "listenUrl": "ftp://localhost:5005" },
              "providers": { "copilot": { "apiKey": "test-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "wildcard-invalid-scheme",
            """
            {
              "gateway": { "listenUrl": "ftp://+:5000" },
              "providers": { "copilot": { "apiKey": "test-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "agent-missing-provider",
            """
            {
              "providers": { "copilot": { "apiKey": "test-key" } },
              "agents": { "assistant": { "model": "gpt-4.1" } }
            }
            """
        },
        {
            "agent-missing-model",
            """
            {
              "providers": { "copilot": { "apiKey": "test-key" } },
              "agents": { "assistant": { "provider": "copilot" } }
            }
            """
        },
        {
            "provider-invalid-baseurl",
            """
            {
              "providers": { "openai-compat": { "apiKey": "compat-key", "baseUrl": "notaurl" } },
              "agents": { "assistant": { "provider": "openai-compat", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "cron-job-missing-schedule",
            """
            {
              "cron": { "jobs": { "daily": { "actionType": "agent-prompt" } } }
            }
            """
        },
        {
            "file-store-missing-filepath",
            """
            {
              "gateway": { "sessionStore": { "type": "File" } },
              "providers": { "copilot": { "apiKey": "test-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "apikey-missing-required-fields",
            """
            {
              "gateway": { "apiKeys": { "tenant-a": { "displayName": "Tenant A" } } },
              "providers": { "copilot": { "apiKey": "provider-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "location-unsupported-type",
            """
            {
              "gateway": { "locations": { "repo": { "type": "git", "path": "." } } },
              "providers": { "copilot": { "apiKey": "provider-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "crossworld-inbound-missing-allowed-worlds",
            """
            {
              "gateway": { "crossWorld": { "inbound": { "enabled": true, "apiKeys": { "world-b": "peer-key" } } } },
              "providers": { "copilot": { "apiKey": "provider-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "crossworld-peer-invalid-endpoint",
            """
            {
              "gateway": { "crossWorld": { "peers": { "world-b": { "endpoint": "notaurl" } }, "inbound": { "enabled": false } } },
              "providers": { "copilot": { "apiKey": "provider-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "cors-origin-invalid-url",
            """
            {
              "gateway": { "cors": { "allowedOrigins": ["notaurl"] } },
              "providers": { "copilot": { "apiKey": "provider-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "agent-defaults-blank-toolid",
            """
            {
              "providers": { "copilot": { "apiKey": "provider-key" } },
              "agents": {
                "defaults": { "toolIds": [""] },
                "assistant": { "provider": "copilot", "model": "gpt-4.1" }
              }
            }
            """
        },
        {
            "memory-prompt-injection-invalid",
            """
            {
              "providers": { "copilot": { "apiKey": "provider-key" } },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1",
                  "memory": { "enabled": true, "promptInjection": "verbose" }
                }
              }
            }
            """
        },
        {
            "invalid-log-level",
            """
            {
              "gateway": { "logLevel": "Verbose" },
              "providers": { "copilot": { "apiKey": "provider-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
        {
            "prompt-template-missing-prompt",
            """
            {
              "promptTemplates": { "summary": { "description": "no prompt body" } },
              "providers": { "copilot": { "apiKey": "provider-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """
        },
    };

    [Theory]
    [MemberData(nameof(ValidConfigs))]
    public async Task ValidConfig_AnnotatedPath_AcceptsExactlyLikeLegacyPath(string name, string json)
    {
        _ = name;
        var config = await LoadAsync(json);

        var legacyErrors = PlatformConfigLoader.Validate(config);
        var annotatedErrors = PlatformConfigLoader.ValidateAnnotated(config);

        // Old path accepts this config -> new path must also accept it (no rule lost).
        legacyErrors.ShouldBeEmpty($"legacy baseline expected '{name}' to be valid");
        annotatedErrors.ShouldBeEmpty(
            $"DataAnnotations path must accept '{name}' exactly like the legacy path. " +
            $"Unexpected errors: {string.Join(" | ", annotatedErrors)}");
    }

    [Theory]
    [MemberData(nameof(InvalidConfigs))]
    public async Task InvalidConfig_AnnotatedPath_RejectsExactlyLikeLegacyPath(string name, string json)
    {
        _ = name;
        var config = await LoadAsync(json);

        var legacyErrors = PlatformConfigLoader.Validate(config);
        var annotatedErrors = PlatformConfigLoader.ValidateAnnotated(config);

        // Old path rejects this config -> new path must also reject it (no rule lost).
        legacyErrors.ShouldNotBeEmpty($"legacy baseline expected '{name}' to be invalid");
        annotatedErrors.ShouldNotBeEmpty(
            $"DataAnnotations path must reject '{name}' exactly like the legacy path, but it accepted it.");
    }

    [Fact]
    public void OptionsValidator_RunsAnnotatedPath_AndAggregatesSchemaErrors()
    {
        // The options validator (startup fast-fail) routes server validation through the
        // DataAnnotations path; a clean config validates clean through it.
        var config = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["copilot"] = new() { ApiKey = "provider-key", DefaultModel = "gpt-4.1" },
            },
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new() { Provider = "copilot", Model = "gpt-4.1", Enabled = true },
            },
        };

        var validator = new PlatformConfigOptionsValidator();
        var result = validator.Validate(name: null, config);

        result.Succeeded.ShouldBeTrue(
            $"Clean config should validate clean through the options validator. Failures: {string.Join(" | ", result.Failures ?? [])}");
    }

    [Fact]
    public void OptionsValidator_InvalidConfig_FailsThroughAnnotatedPath()
    {
        // A non-quarantinable cross-field error (here: the reserved agents.defaults pseudo-agent,
        // whose values seed every agent) is rejected by the cross-field escape hatch surfaced
        // through Validator.TryValidateObject. Note: per #2102 an error scoped to a specific named
        // agent (e.g. a broken 'agents.broken' missing provider/model) is quarantined instead of
        // failing global options, so it is deliberately NOT used here.
        var config = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["copilot"] = new() { ApiKey = "provider-key" },
            },
            AgentDefaults = new AgentDefaultsConfig
            {
                Heartbeat = new BotNexus.Gateway.Abstractions.Models.HeartbeatAgentConfig
                {
                    IntervalMinutes = 0,
                },
            },
        };

        var validator = new PlatformConfigOptionsValidator();
        var result = validator.Validate(name: null, config);

        result.Succeeded.ShouldBeFalse("a non-agent-scoped cross-field error must fail validation");
    }

    private static async Task<PlatformConfig> LoadAsync(string json)
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "botnexus-config-dataannotations-parity-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var configPath = Path.Combine(rootPath, "config.json");

        try
        {
            await File.WriteAllTextAsync(configPath, json);
            return await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }
}
