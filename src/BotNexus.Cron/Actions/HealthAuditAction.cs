using BotNexus.Core.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BotNexus.Cron.Actions;

public sealed class HealthAuditAction(HealthCheckService? healthCheckService = null) : ISystemAction
{
    private readonly HealthCheckService? _healthCheckService = healthCheckService;

    public string Name => "health-audit";
    public string Description => "Runs internal health checks and reports status.";

    public async Task<string> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (_healthCheckService is null)
            return "[health-audit] Health check service is not registered.";

        var report = await _healthCheckService.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        var entries = report.Entries
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => $"{pair.Key}: {pair.Value.Status}");
        var summary = string.Join(", ", entries);

        return string.IsNullOrWhiteSpace(summary)
            ? $"[health-audit] Overall status: {report.Status}. No health checks registered."
            : $"[health-audit] Overall status: {report.Status}. Checks: {summary}";
    }
}
