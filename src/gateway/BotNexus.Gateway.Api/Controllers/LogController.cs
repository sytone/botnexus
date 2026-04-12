using Microsoft.AspNetCore.Mvc;
using BotNexus.Gateway.Api.Logging;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Receives client-side log entries for unified server-side debugging.
/// </summary>
/// <summary>
/// Represents log controller.
/// </summary>
[ApiController]
[Route("api/log")]
[Route("api/logs")]
public sealed class LogController : ControllerBase
{
    private readonly ILogger<LogController> _logger;
    private readonly IRecentLogStore _recentLogs;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogController"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="recentLogs">The recent log store.</param>
    public LogController(ILogger<LogController> logger, IRecentLogStore recentLogs)
    {
        _logger = logger;
        _recentLogs = recentLogs;
    }

    /// <summary>Receives a log entry from the WebUI client.</summary>
    /// <summary>
    /// Executes post.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The post result.</returns>
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

    /// <summary>Returns recent structured log entries for diagnostics.</summary>
    /// <summary>
    /// Executes get recent.
    /// </summary>
    /// <param name="limit">The limit.</param>
    /// <returns>The get recent result.</returns>
    [HttpGet("recent")]
    public ActionResult<IReadOnlyList<RecentLogEntry>> GetRecent([FromQuery] int limit = 100)
    {
        return Ok(_recentLogs.GetRecent(limit));
    }
}

/// <summary>Client log entry payload.</summary>
public sealed record ClientLogEntry(string? Level, string? Message, string? Data, string? Version, string? Timestamp);
