using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.IO.Abstractions;
using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Tests;

public sealed class ConfigControllerTests
{
    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    [Fact]
    public async Task Validate_WhenFileMissing_ReturnsInvalidResultWithActionableError()
    {
        var controller = new ConfigController();
        var missingPath = Path.Combine(Path.GetTempPath(), "botnexus-config-tests", Guid.NewGuid().ToString("N"), "config.json");

        var result = await controller.Validate(
            missingPath,
            new TestOptionsMonitor<PlatformConfig>(new PlatformConfig()),
            EmptyConfiguration,
            CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var payload = ok.Value.ShouldBeOfType<ConfigValidationResponse>();
        payload.IsValid.ShouldBeFalse();
        payload.Errors.ShouldContain(e => e.Contains("Config file not found", StringComparison.Ordinal));
        payload.Errors.ShouldContain(e => e.Contains("Create ~/.botnexus/config.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_WhenConfigInvalid_ReturnsValidationErrors()
    {
        var controller = new ConfigController();
        var root = Path.Combine(Path.GetTempPath(), "botnexus-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, """{"apiKeys":{"tenant-a":{}}}""");

            var result = await controller.Validate(
                path,
                new TestOptionsMonitor<PlatformConfig>(new PlatformConfig()),
                EmptyConfiguration,
                CancellationToken.None);

            var ok = result.Result.ShouldBeOfType<OkObjectResult>();
            var payload = ok.Value.ShouldBeOfType<ConfigValidationResponse>();
            payload.IsValid.ShouldBeFalse();
            payload.Errors.ShouldContain(e => e.Contains("gateway.apiKeys.tenant-a.apiKey", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_WhenPathNotProvided_UsesCurrentOptionsMonitorValue()
    {
        var controller = new ConfigController();
        var root = Path.Combine(Path.GetTempPath(), "botnexus-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "config.json");
        await File.WriteAllTextAsync(configPath, "{}");
        var options = new TestOptionsMonitor<PlatformConfig>(new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new()
            }
        });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotNexus:ConfigPath"] = configPath
            })
            .Build();
        try
        {
            var result = await controller.Validate(null, options, configuration, CancellationToken.None);

            var ok = result.Result.ShouldBeOfType<OkObjectResult>();
            var payload = ok.Value.ShouldBeOfType<ConfigValidationResponse>();
            payload.IsValid.ShouldBeFalse();
            payload.ConfigPath.ShouldBe(configPath);
            payload.Errors.ShouldContain(error => error.Contains("agents.assistant.provider", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_WhenExplicitPathProvided_ValidatesThatPath_NotCurrentOptions()
    {
        var controller = new ConfigController();
        var root = Path.Combine(Path.GetTempPath(), "botnexus-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var explicitPath = Path.Combine(root, "explicit.json");
        var configuredPath = Path.Combine(root, "configured.json");
        try
        {
            await File.WriteAllTextAsync(explicitPath, """{"apiKeys":{"tenant-a":{}}}""");
            await File.WriteAllTextAsync(configuredPath, """{"gateway":{"listenUrl":"http://localhost:8080"}}""");

            var options = new TestOptionsMonitor<PlatformConfig>(new PlatformConfig
            {
                Agents = new Dictionary<string, AgentDefinitionConfig>
                {
                    ["assistant"] = new()
                }
            });
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BotNexus:ConfigPath"] = configuredPath
                })
                .Build();

            var result = await controller.Validate(explicitPath, options, configuration, CancellationToken.None);

            var ok = result.Result.ShouldBeOfType<OkObjectResult>();
            var payload = ok.Value.ShouldBeOfType<ConfigValidationResponse>();
            payload.ConfigPath.ShouldBe(explicitPath);
            payload.Errors.ShouldContain(e => e.Contains("gateway.apiKeys.tenant-a.apiKey", StringComparison.Ordinal));
            payload.Errors.ShouldNotContain(e => e.Contains("agents.assistant.provider", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_WhenExplicitPathContainsInvalidJson_ReturnsJsonError()
    {
        var controller = new ConfigController();
        var root = Path.Combine(Path.GetTempPath(), "botnexus-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "invalid.json");
        try
        {
            await File.WriteAllTextAsync(path, """{"gateway":""");

            var result = await controller.Validate(
                path,
                new TestOptionsMonitor<PlatformConfig>(new PlatformConfig()),
                EmptyConfiguration,
                CancellationToken.None);

            var ok = result.Result.ShouldBeOfType<OkObjectResult>();
            var payload = ok.Value.ShouldBeOfType<ConfigValidationResponse>();
            payload.IsValid.ShouldBeFalse();
            payload.Errors.ShouldContain(error => error.Contains("Invalid JSON in config file", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // -----------------------------------------------------------------
    // GetSection secret redaction (#1516)
    // The per-section read must mask EVERY secret-bearing section, not just providers.
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetSection_GatewaySection_RedactsApiKeysConnectionStringsAndCrossWorldSecrets()
    {
        const string raw = """
        {
          "gateway": {
            "listenUrl": "http://localhost:5000",
            "apiKeys": { "tenant-a": { "apiKey": "super-secret-key" } },
            "sessionStore": { "connectionString": "Data Source=secret.db;Password=hunter2" },
            "locations": { "primary": { "connectionString": "Server=db;Password=loc-secret" } },
            "crossWorld": {
              "peers": { "peer-1": { "apiKey": "peer-secret" } },
              "inbound": { "apiKeys": { "inbound-1": "inbound-secret" } }
            }
          }
        }
        """;

        await WithConfigFileAsync(raw, async (controller, writer) =>
        {
            var result = await controller.GetSection("gateway", writer, CancellationToken.None);
            var ok = result.Result.ShouldBeOfType<OkObjectResult>();
            var gateway = ok.Value.ShouldBeOfType<JsonObject>();

            // Non-secret values are preserved.
            gateway["listenUrl"]!.GetValue<string>().ShouldBe("http://localhost:5000");

            // Every secret-bearing field is masked.
            gateway["apiKeys"]!["tenant-a"]!["apiKey"]!.GetValue<string>().ShouldBe("***");
            gateway["sessionStore"]!["connectionString"]!.GetValue<string>().ShouldBe("***");
            gateway["locations"]!["primary"]!["connectionString"]!.GetValue<string>().ShouldBe("***");
            gateway["crossWorld"]!["peers"]!["peer-1"]!["apiKey"]!.GetValue<string>().ShouldBe("***");
            gateway["crossWorld"]!["inbound"]!["apiKeys"]!["inbound-1"]!.GetValue<string>().ShouldBe("***");

            // The plaintext secrets must not appear anywhere in the serialized payload.
            var serialized = gateway.ToJsonString();
            serialized.ShouldNotContain("super-secret-key");
            serialized.ShouldNotContain("hunter2");
            serialized.ShouldNotContain("loc-secret");
            serialized.ShouldNotContain("peer-secret");
            serialized.ShouldNotContain("inbound-secret");
        });
    }

    [Fact]
    public async Task GetSection_ProvidersSection_StillRedactsApiKeys()
    {
        const string raw = """
        {
          "providers": {
            "openai": { "apiKey": "sk-provider-secret", "model": "gpt-4.1" }
          }
        }
        """;

        await WithConfigFileAsync(raw, async (controller, writer) =>
        {
            var result = await controller.GetSection("providers", writer, CancellationToken.None);
            var ok = result.Result.ShouldBeOfType<OkObjectResult>();
            var providers = ok.Value.ShouldBeOfType<JsonObject>();

            providers["openai"]!["apiKey"]!.GetValue<string>().ShouldBe("***");
            providers["openai"]!["model"]!.GetValue<string>().ShouldBe("gpt-4.1");
            providers.ToJsonString().ShouldNotContain("sk-provider-secret");
        });
    }

    [Fact]
    public async Task GetSection_NonSecretSection_ReturnsContentUnchanged()
    {
        const string raw = """
        {
          "channels": { "signalr": { "enabled": true } }
        }
        """;

        await WithConfigFileAsync(raw, async (controller, writer) =>
        {
            var result = await controller.GetSection("channels", writer, CancellationToken.None);
            var ok = result.Result.ShouldBeOfType<OkObjectResult>();
            var channels = ok.Value.ShouldBeOfType<JsonObject>();

            channels["signalr"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
        });
    }

    [Fact]
    public async Task GetSection_MissingSection_ReturnsNotFound()
    {
        const string raw = """{ "gateway": { "listenUrl": "http://localhost:5000" } }""";

        await WithConfigFileAsync(raw, async (controller, writer) =>
        {
            var result = await controller.GetSection("does-not-exist", writer, CancellationToken.None);
            result.Result.ShouldBeOfType<NotFoundResult>();
        });
    }

    private static async Task WithConfigFileAsync(
        string rawJson,
        Func<ConfigController, PlatformConfigWriter, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "botnexus-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, rawJson);
            var controller = new ConfigController();
            var writer = new PlatformConfigWriter(path, new FileSystem());
            await body(controller, writer);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
