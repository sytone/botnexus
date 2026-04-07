using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Receives client-side log entries for unified server-side debugging.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class LogController : ControllerBase
{
    private readonly ILogger<LogController> _logger;

    public LogController(ILogger<LogController> logger) => _logger = logger;

    /// <summary>Receives a log entry from the WebUI client.</summary>
    [HttpPost]
    public IActionResult Post([FromBody] ClientLogEntry entry)
    {
        var level = (entry.Level?.ToLowerInvariant()) switch
        {
            "error" or "err" => LogLevel.Error,
            "warn" or "warning" => LogLevel.Warning,
            "debug" => LogLevel.Debug,
            _ => LogLevel.Information
        };

        _logger.Log(level, "WebUI[v{Version}]: {Message} {Data}",
            entry.Version ?? "?", entry.Message ?? "", entry.Data ?? "");

        return Ok();
    }
}

/// <summary>Client log entry payload.</summary>
public sealed record ClientLogEntry(string? Level, string? Message, string? Data, string? Version, string? Timestamp);
