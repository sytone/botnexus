using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for platform configuration diagnostics.
/// </summary>
/// <summary>
/// Represents config controller.
/// </summary>
[ApiController]
[Route("api/config")]
public sealed class ConfigController : ControllerBase
{
    /// <summary>
    /// Validates the platform configuration file and returns any errors.
    /// </summary>
    /// <param name="path">Optional explicit path to a config file. Defaults to <c>~/.botnexus/config.json</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The config validation result.</returns>
    [HttpGet("validate")]
    public async Task<ActionResult<ConfigValidationResponse>> Validate([FromQuery] string? path, CancellationToken cancellationToken)
    {
        var resolvedPath = string.IsNullOrWhiteSpace(path)
            ? PlatformConfigLoader.DefaultConfigPath
            : Path.GetFullPath(path);

        if (!System.IO.File.Exists(resolvedPath))
        {
            return Ok(new ConfigValidationResponse(
                IsValid: false,
                ConfigPath: resolvedPath,
                Warnings: [],
                Errors:
                [
                    $"Config file not found at '{resolvedPath}'.",
                    "Create ~/.botnexus/config.json (or pass ?path=...) and include gateway/providers/channels/agents sections."
                ]));
        }

        try
        {
            var config = await PlatformConfigLoader.LoadAsync(resolvedPath, cancellationToken);
            var warnings = PlatformConfigLoader.ValidateWarnings(config);
            return Ok(new ConfigValidationResponse(true, resolvedPath, warnings, []));
        }
        catch (OptionsValidationException ex)
        {
            var errors = ex.Failures
                .Where(error => !string.IsNullOrWhiteSpace(error))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(error => error, StringComparer.Ordinal)
                .ToArray();
            return Ok(new ConfigValidationResponse(false, resolvedPath, [], errors));
        }
    }
}

/// <summary>
/// Result of a platform configuration validation check.
/// </summary>
/// <param name="IsValid">Whether the configuration passed all validation rules.</param>
/// <param name="ConfigPath">Resolved path to the configuration file that was validated.</param>
/// <param name="Warnings">Validation warnings that do not block startup.</param>
/// <param name="Errors">Validation errors, empty when <paramref name="IsValid"/> is <see langword="true"/>.</param>
public sealed record ConfigValidationResponse(
    bool IsValid,
    string ConfigPath,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
