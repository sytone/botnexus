using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Api.Controllers;

[ApiController]
[Route("api/config")]
public sealed class ConfigController : ControllerBase
{
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
                Errors: [$"Config file not found at '{resolvedPath}'. Create it under ~/.botnexus/config.json."]));
        }

        try
        {
            await PlatformConfigLoader.LoadAsync(resolvedPath, cancellationToken);
            return Ok(new ConfigValidationResponse(true, resolvedPath, []));
        }
        catch (OptionsValidationException ex)
        {
            return Ok(new ConfigValidationResponse(false, resolvedPath, ex.Failures.ToArray()));
        }
    }
}

public sealed record ConfigValidationResponse(bool IsValid, string ConfigPath, IReadOnlyList<string> Errors);
