using BotNexus.Gateway.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

public sealed class ConfigControllerTests
{
    [Fact]
    public async Task Validate_WhenFileMissing_ReturnsInvalidResultWithActionableError()
    {
        var controller = new ConfigController();
        var missingPath = Path.Combine(Path.GetTempPath(), "botnexus-config-tests", Guid.NewGuid().ToString("N"), "config.json");

        var result = await controller.Validate(missingPath, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<ConfigValidationResponse>().Subject;
        payload.IsValid.Should().BeFalse();
        payload.Errors.Should().ContainSingle(e => e.Contains("Config file not found", StringComparison.Ordinal));
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

            var result = await controller.Validate(path, CancellationToken.None);

            var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
            var payload = ok.Value.Should().BeOfType<ConfigValidationResponse>().Subject;
            payload.IsValid.Should().BeFalse();
            payload.Errors.Should().Contain(e => e.Contains("gateway.apiKeys.tenant-a.apiKey", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
