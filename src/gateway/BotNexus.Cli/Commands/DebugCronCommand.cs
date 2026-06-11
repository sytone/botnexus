using System.CommandLine;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// CLI subcommands for inspecting the cron SQLite database directly
/// without requiring a running gateway instance. Provides status, history,
/// and missed-job detection for offline scheduler diagnostics.
/// </summary>
internal sealed class DebugCronCommand
{
    public Command Build(Option<string?> targetOption)
    {
        var command = new Command("cron", "Inspect cron scheduler state directly (offline, no gateway required).");

        var formatOption = new Option<string>("--format", () => "table", "Output format: table or json.");
        command.AddOption(formatOption);

        // ── status ──
        var statusCommand = new Command("status", "Show scheduler health: enabled jobs, last/next tick.");
        statusCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var dbPath = ResolveCronDb(target);
            context.ExitCode = ExecuteStatus(dbPath, format);
            return Task.CompletedTask;
        });

        // ── history ──
        var limitOption = new Option<int>("--limit", () => 20, "Maximum run records to return.");
        var jobIdOption = new Option<string?>("--job", "Filter by job ID.");
        var historyCommand = new Command("history", "Show recent run outcomes from cron.sqlite.")
        {
            limitOption, jobIdOption
        };
        historyCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var limit = context.ParseResult.GetValueForOption(limitOption);
            var jobId = context.ParseResult.GetValueForOption(jobIdOption);
            var dbPath = ResolveCronDb(target);
            context.ExitCode = ExecuteHistory(dbPath, jobId, limit, format);
            return Task.CompletedTask;
        });

        // ── missed ──
        var missedCommand = new Command("missed", "Identify jobs that should have fired but did not.");
        missedCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var dbPath = ResolveCronDb(target);
            context.ExitCode = ExecuteMissed(dbPath, format);
            return Task.CompletedTask;
        });

        command.AddCommand(statusCommand);
        command.AddCommand(historyCommand);
        command.AddCommand(missedCommand);
        return command;
    }

    internal static string ResolveCronDb(string? target)
    {
        var home = CliPaths.ResolveTarget(target);
        return Path.Combine(home, "cron.sqlite");
    }

    internal static int ExecuteStatus(string dbPath, string format)
    {
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLine("[red]cron.sqlite not found at:[/] " + Markup.Escape(dbPath));
            return 1;
        }

        using var connection = OpenReadOnly(dbPath);
        var status = new CronStatus();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM cron_jobs";
            status.TotalJobs = Convert.ToInt32(cmd.ExecuteScalar());
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM cron_jobs WHERE enabled = 1";
            status.EnabledJobs = Convert.ToInt32(cmd.ExecuteScalar());
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM cron_jobs WHERE enabled = 0";
            status.DisabledJobs = Convert.ToInt32(cmd.ExecuteScalar());
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT MAX(last_run_at) FROM cron_jobs WHERE last_run_at IS NOT NULL";
            var result = cmd.ExecuteScalar();
            status.LastRunAt = result is DBNull or null ? null : result.ToString();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT MIN(next_run_at) FROM cron_jobs WHERE enabled = 1 AND next_run_at IS NOT NULL";
            var result = cmd.ExecuteScalar();
            status.NextRunAt = result is DBNull or null ? null : result.ToString();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM cron_runs WHERE status = 'running'";
            status.RunningNow = Convert.ToInt32(cmd.ExecuteScalar());
        }

        var fileInfo = new FileInfo(dbPath);
        status.DbSizeBytes = fileInfo.Length;

        if (format == "json")
        {
            AnsiConsole.Write(new Text(JsonSerializer.Serialize(status, DebugSessionsCommand.JsonOpts)));
            AnsiConsole.WriteLine();
            return 0;
        }

        AnsiConsole.MarkupLine("[bold]Cron Scheduler Status[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  Total jobs:      [bold]{status.TotalJobs}[/]");
        AnsiConsole.MarkupLine($"  Enabled:         [green]{status.EnabledJobs}[/]");
        AnsiConsole.MarkupLine($"  Disabled:        [dim]{status.DisabledJobs}[/]");
        AnsiConsole.MarkupLine($"  Currently running: [bold]{status.RunningNow}[/]");
        AnsiConsole.MarkupLine($"  Last run at:     {Markup.Escape(FormatDate(status.LastRunAt))}");
        AnsiConsole.MarkupLine($"  Next scheduled:  {Markup.Escape(FormatDate(status.NextRunAt))}");
        AnsiConsole.MarkupLine($"  DB size:         {Math.Round(status.DbSizeBytes / 1024.0, 1)} KB");
        return 0;
    }

    internal static int ExecuteHistory(string dbPath, string? jobId, int limit, string format)
    {
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLine("[red]cron.sqlite not found at:[/] " + Markup.Escape(dbPath));
            return 1;
        }

        using var connection = OpenReadOnly(dbPath);
        using var cmd = connection.CreateCommand();

        if (jobId is not null)
        {
            cmd.CommandText = """
                SELECT r.id, r.job_id, j.name, r.started_at, r.completed_at, r.status, r.error
                FROM cron_runs r
                LEFT JOIN cron_jobs j ON j.id = r.job_id
                WHERE r.job_id = @jobId
                ORDER BY r.started_at DESC
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@jobId", jobId);
        }
        else
        {
            cmd.CommandText = """
                SELECT r.id, r.job_id, j.name, r.started_at, r.completed_at, r.status, r.error
                FROM cron_runs r
                LEFT JOIN cron_jobs j ON j.id = r.job_id
                ORDER BY r.started_at DESC
                LIMIT @limit
                """;
        }
        cmd.Parameters.AddWithValue("@limit", limit);

        var runs = new List<CronRunEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var startedAt = reader.IsDBNull(3) ? null : reader.GetString(3);
            var completedAt = reader.IsDBNull(4) ? null : reader.GetString(4);
            long? durationMs = null;
            if (startedAt is not null && completedAt is not null &&
                DateTimeOffset.TryParse(startedAt, out var start) &&
                DateTimeOffset.TryParse(completedAt, out var end))
            {
                durationMs = (long)(end - start).TotalMilliseconds;
            }

            runs.Add(new CronRunEntry
            {
                RunId = reader.GetString(0),
                JobId = reader.GetString(1),
                JobName = reader.IsDBNull(2) ? null : reader.GetString(2),
                StartedAt = startedAt,
                CompletedAt = completedAt,
                Status = reader.GetString(5),
                Error = reader.IsDBNull(6) ? null : reader.GetString(6),
                DurationMs = durationMs
            });
        }

        if (format == "json")
        {
            AnsiConsole.Write(new Text(JsonSerializer.Serialize(runs, DebugSessionsCommand.JsonOpts)));
            AnsiConsole.WriteLine();
            return 0;
        }

        if (runs.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No run history found.[/]");
            return 0;
        }

        var table = new Table()
            .AddColumn("Job")
            .AddColumn("Started")
            .AddColumn("Duration")
            .AddColumn("Status")
            .AddColumn("Error");

        foreach (var r in runs)
        {
            table.AddRow(
                Markup.Escape(Truncate(r.JobName ?? r.JobId, 28)),
                Markup.Escape(FormatDate(r.StartedAt)),
                r.DurationMs.HasValue ? $"{r.DurationMs}ms" : "[dim]—[/]",
                FormatRunStatus(r.Status),
                Markup.Escape(Truncate(r.Error ?? "", 40)));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[dim]{runs.Count} run(s) shown.[/]");
        return 0;
    }

    internal static int ExecuteMissed(string dbPath, string format)
    {
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLine("[red]cron.sqlite not found at:[/] " + Markup.Escape(dbPath));
            return 1;
        }

        using var connection = OpenReadOnly(dbPath);
        using var cmd = connection.CreateCommand();

        // A job is "missed" if it's enabled, has a next_run_at in the past, and isn't currently running
        var now = DateTimeOffset.UtcNow.ToString("o");
        cmd.CommandText = """
            SELECT j.id, j.name, j.schedule, j.last_run_at, j.next_run_at
            FROM cron_jobs j
            WHERE j.enabled = 1
              AND j.next_run_at IS NOT NULL
              AND j.next_run_at < @now
              AND j.id NOT IN (SELECT job_id FROM cron_runs WHERE status = 'running')
            ORDER BY j.next_run_at ASC
            """;
        cmd.Parameters.AddWithValue("@now", now);

        var missed = new List<CronMissedEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var nextRunAt = reader.IsDBNull(4) ? null : reader.GetString(4);
            TimeSpan? overdue = null;
            if (nextRunAt is not null && DateTimeOffset.TryParse(nextRunAt, out var next))
            {
                overdue = DateTimeOffset.UtcNow - next;
            }

            missed.Add(new CronMissedEntry
            {
                JobId = reader.GetString(0),
                Name = reader.IsDBNull(1) ? null : reader.GetString(1),
                Schedule = reader.GetString(2),
                LastRunAt = reader.IsDBNull(3) ? null : reader.GetString(3),
                NextRunAt = nextRunAt,
                OverdueMinutes = overdue.HasValue ? (long)overdue.Value.TotalMinutes : null
            });
        }

        if (format == "json")
        {
            AnsiConsole.Write(new Text(JsonSerializer.Serialize(missed, DebugSessionsCommand.JsonOpts)));
            AnsiConsole.WriteLine();
            return 0;
        }

        if (missed.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No missed jobs — scheduler appears healthy.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[red]⚠ {missed.Count} job(s) appear to have missed their scheduled run:[/]");
        AnsiConsole.WriteLine();

        var table = new Table()
            .AddColumn("Job")
            .AddColumn("Schedule")
            .AddColumn("Expected At")
            .AddColumn("Overdue");

        foreach (var m in missed)
        {
            table.AddRow(
                Markup.Escape(Truncate(m.Name ?? m.JobId, 28)),
                Markup.Escape(m.Schedule),
                Markup.Escape(FormatDate(m.NextRunAt)),
                m.OverdueMinutes.HasValue ? $"[red]{m.OverdueMinutes} min[/]" : "[dim]—[/]");
        }

        AnsiConsole.Write(table);
        return 0;
    }

    // ── Helpers ──

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        return connection;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";

    private static string FormatDate(string? isoDate)
    {
        if (isoDate is null) return "(none)";
        if (DateTimeOffset.TryParse(isoDate, out var dto))
            return dto.ToString("yyyy-MM-dd HH:mm:ss");
        return isoDate;
    }

    private static string FormatRunStatus(string status) => status.ToLowerInvariant() switch
    {
        "completed" => "[green]completed[/]",
        "running" => "[blue]running[/]",
        "failed" => "[red]failed[/]",
        "timeout" => "[yellow]timeout[/]",
        _ => Markup.Escape(status)
    };

    // ── DTOs ──

    internal sealed class CronStatus
    {
        public int TotalJobs { get; set; }
        public int EnabledJobs { get; set; }
        public int DisabledJobs { get; set; }
        public int RunningNow { get; set; }
        public string? LastRunAt { get; set; }
        public string? NextRunAt { get; set; }
        public long DbSizeBytes { get; set; }
    }

    internal sealed class CronRunEntry
    {
        public string RunId { get; set; } = "";
        public string JobId { get; set; } = "";
        public string? JobName { get; set; }
        public string? StartedAt { get; set; }
        public string? CompletedAt { get; set; }
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public long? DurationMs { get; set; }
    }

    internal sealed class CronMissedEntry
    {
        public string JobId { get; set; } = "";
        public string? Name { get; set; }
        public string Schedule { get; set; } = "";
        public string? LastRunAt { get; set; }
        public string? NextRunAt { get; set; }
        public long? OverdueMinutes { get; set; }
    }
}
