using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

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
}
