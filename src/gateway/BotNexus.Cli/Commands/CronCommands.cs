using System.CommandLine;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Cron;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// CLI sub-commands for managing cron jobs via the gateway REST API.
/// </summary>
internal sealed class CronCommands
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Constructor used directly from tests (inject a pre-configured HttpClient).
    /// </summary>
    public CronCommands(HttpClient http) => _http = http;

    /// <summary>
    /// Constructor used from DI / Program.cs (creates a default client).
    /// </summary>
    public CronCommands() : this(new HttpClient()) { }

    // ──────────────────────────────────────────────────────────────────────
    //  Command tree
    // ──────────────────────────────────────────────────────────────────────

    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var command = new Command("cron", "Manage cron jobs on a running BotNexus gateway.");

        var urlOption = new Option<string>("--url", () => "http://localhost:5005", "Gateway base URL.");

        // ── list ──────────────────────────────────────────────────────────
        var listCommand = new Command("list", "List all cron jobs.") { urlOption };
        listCommand.SetHandler(async context =>
        {
            var url = context.ParseResult.GetValueForOption(urlOption)!;
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            _http.BaseAddress ??= new Uri(url);
            context.ExitCode = await ExecuteListAsync(verbose, context.GetCancellationToken());
        });

        // ── get ───────────────────────────────────────────────────────────
        var jobIdArg = new Argument<string>("job-id", "Cron job ID.");
        var getCommand = new Command("get", "Show details for a single cron job.") { jobIdArg, urlOption };
        getCommand.SetHandler(async context =>
        {
            var id = context.ParseResult.GetValueForArgument(jobIdArg);
            var url = context.ParseResult.GetValueForOption(urlOption)!;
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            _http.BaseAddress ??= new Uri(url);
            context.ExitCode = await ExecuteGetAsync(id, verbose, context.GetCancellationToken());
        });

        // ── delete ────────────────────────────────────────────────────────
        var deleteCommand = new Command("delete", "Delete a cron job.") { jobIdArg, urlOption };
        deleteCommand.SetHandler(async context =>
        {
            var id = context.ParseResult.GetValueForArgument(jobIdArg);
            var url = context.ParseResult.GetValueForOption(urlOption)!;
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            _http.BaseAddress ??= new Uri(url);
            context.ExitCode = await ExecuteDeleteAsync(id, verbose, context.GetCancellationToken());
        });

        // ── run ───────────────────────────────────────────────────────────
        var runCommand = new Command("run", "Trigger a cron job immediately.") { jobIdArg, urlOption };
        runCommand.SetHandler(async context =>
        {
            var id = context.ParseResult.GetValueForArgument(jobIdArg);
            var url = context.ParseResult.GetValueForOption(urlOption)!;
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            _http.BaseAddress ??= new Uri(url);
            context.ExitCode = await ExecuteRunAsync(id, verbose, context.GetCancellationToken());
        });

        // ── enable / disable ──────────────────────────────────────────────
        var enableCommand = new Command("enable", "Enable a cron job.") { jobIdArg, urlOption };
        enableCommand.SetHandler(async context =>
        {
            var id = context.ParseResult.GetValueForArgument(jobIdArg);
            var url = context.ParseResult.GetValueForOption(urlOption)!;
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            _http.BaseAddress ??= new Uri(url);
            context.ExitCode = await ExecuteEnableAsync(id, enable: true, verbose, context.GetCancellationToken());
        });

        var disableCommand = new Command("disable", "Disable a cron job.") { jobIdArg, urlOption };
        disableCommand.SetHandler(async context =>
        {
            var id = context.ParseResult.GetValueForArgument(jobIdArg);
            var url = context.ParseResult.GetValueForOption(urlOption)!;
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            _http.BaseAddress ??= new Uri(url);
            context.ExitCode = await ExecuteEnableAsync(id, enable: false, verbose, context.GetCancellationToken());
        });

        command.AddCommand(listCommand);
        command.AddCommand(getCommand);
        command.AddCommand(deleteCommand);
        command.AddCommand(runCommand);
        command.AddCommand(enableCommand);
        command.AddCommand(disableCommand);
        return command;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Internal execute methods (internal so tests can call them directly)
    // ──────────────────────────────────────────────────────────────────────

    internal async Task<int> ExecuteListAsync(bool verbose, CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync("api/cron", ct);
            if (!response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Gateway returned HTTP {(int)response.StatusCode}.");
                return 1;
            }

            var jobs = await response.Content.ReadFromJsonAsync<List<CronJob>>(JsonOpts, ct) ?? [];

            if (jobs.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No cron jobs found.[/]");
                return 0;
            }

            var table = new Table().AddColumn("ID").AddColumn("Name").AddColumn("Schedule").AddColumn("Agent").AddColumn("Enabled").AddColumn("Type");
            foreach (var job in jobs)
            {
                var enabledMark = job.Enabled ? "[green]\u2713[/]" : "[red]\u2717[/]";
                table.AddRow(
                    Markup.Escape(job.Id.Value),
                    Markup.Escape(job.Name ?? "-"),
                    Markup.Escape(job.Schedule ?? "-"),
                    Markup.Escape(job.AgentId?.Value ?? "-"),
                    enabledMark,
                    Markup.Escape(job.ActionType ?? "-"));
            }

            AnsiConsole.Write(table);
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Could not connect to gateway \u2014 {Markup.Escape(ex.Message)}");
            if (verbose)
                AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    internal async Task<int> ExecuteGetAsync(string jobId, bool verbose, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] job-id is required.");
            return 1;
        }

        try
        {
            var response = await _http.GetAsync($"api/cron/{Uri.EscapeDataString(jobId)}", ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Cron job [yellow]{Markup.Escape(jobId)}[/] not found.");
                return 1;
            }

            if (!response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Gateway returned HTTP {(int)response.StatusCode}.");
                return 1;
            }

            var job = await response.Content.ReadFromJsonAsync<CronJob>(JsonOpts, ct);
            if (job is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Unexpected empty response from gateway.");
                return 1;
            }

            PrintJob(job);
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Could not connect to gateway \u2014 {Markup.Escape(ex.Message)}");
            if (verbose)
                AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    internal async Task<int> ExecuteDeleteAsync(string jobId, bool verbose, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] job-id is required.");
            return 1;
        }

        try
        {
            var response = await _http.DeleteAsync($"api/cron/{Uri.EscapeDataString(jobId)}", ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Cron job [yellow]{Markup.Escape(jobId)}[/] not found.");
                return 1;
            }

            if (!response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Gateway returned HTTP {(int)response.StatusCode}.");
                return 1;
            }

            AnsiConsole.MarkupLine($"[green]\u2713[/] Cron job [yellow]{Markup.Escape(jobId)}[/] deleted.");
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Could not connect to gateway \u2014 {Markup.Escape(ex.Message)}");
            if (verbose)
                AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    internal async Task<int> ExecuteRunAsync(string jobId, bool verbose, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] job-id is required.");
            return 1;
        }

        try
        {
            var response = await _http.PostAsync($"api/cron/{Uri.EscapeDataString(jobId)}/run", null, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Cron job [yellow]{Markup.Escape(jobId)}[/] not found.");
                return 1;
            }

            if (!response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Gateway returned HTTP {(int)response.StatusCode}.");
                return 1;
            }

            var run = await response.Content.ReadFromJsonAsync<CronRun>(JsonOpts, ct);
            AnsiConsole.MarkupLine($"[green]\u2713[/] Cron job [yellow]{Markup.Escape(jobId)}[/] triggered. Run ID: [dim]{Markup.Escape(run?.Id.Value ?? "?")}[/]");
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Could not connect to gateway \u2014 {Markup.Escape(ex.Message)}");
            if (verbose)
                AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    internal async Task<int> ExecuteEnableAsync(string jobId, bool enable, bool verbose, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] job-id is required.");
            return 1;
        }

        try
        {
            // GET existing job
            var getResponse = await _http.GetAsync($"api/cron/{Uri.EscapeDataString(jobId)}", ct);
            if (getResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Cron job [yellow]{Markup.Escape(jobId)}[/] not found.");
                return 1;
            }

            if (!getResponse.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Gateway returned HTTP {(int)getResponse.StatusCode}.");
                return 1;
            }

            var existing = await getResponse.Content.ReadFromJsonAsync<CronJob>(JsonOpts, ct);
            if (existing is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Unexpected empty response from gateway.");
                return 1;
            }

            var updated = existing with { Enabled = enable };

            // PUT updated job
            var putResponse = await _http.PutAsJsonAsync($"api/cron/{Uri.EscapeDataString(jobId)}", updated, JsonOpts, ct);
            if (!putResponse.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Gateway returned HTTP {(int)putResponse.StatusCode} on update.");
                return 1;
            }

            var verb = enable ? "enabled" : "disabled";
            AnsiConsole.MarkupLine($"[green]\u2713[/] Cron job [yellow]{Markup.Escape(jobId)}[/] {verb}.");
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Could not connect to gateway \u2014 {Markup.Escape(ex.Message)}");
            if (verbose)
                AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static void PrintJob(CronJob job)
    {
        AnsiConsole.MarkupLine($"[bold]ID:[/]       {Markup.Escape(job.Id.Value)}");
        AnsiConsole.MarkupLine($"[bold]Name:[/]     {Markup.Escape(job.Name ?? "-")}");
        AnsiConsole.MarkupLine($"[bold]Schedule:[/] {Markup.Escape(job.Schedule ?? "-")}");
        AnsiConsole.MarkupLine($"[bold]Type:[/]     {Markup.Escape(job.ActionType ?? "-")}");
        AnsiConsole.MarkupLine($"[bold]Agent:[/]    {Markup.Escape(job.AgentId?.Value ?? "-")}");
        AnsiConsole.MarkupLine($"[bold]Enabled:[/]  {(job.Enabled ? "[green]yes[/]" : "[red]no[/]")}");
        if (!string.IsNullOrWhiteSpace(job.TimeZone))
            AnsiConsole.MarkupLine($"[bold]TimeZone:[/] {Markup.Escape(job.TimeZone)}");
        if (!string.IsNullOrWhiteSpace(job.Message))
            AnsiConsole.MarkupLine($"[bold]Message:[/]  {Markup.Escape(job.Message)}");
        if (job.CreatedAt != default)
            AnsiConsole.MarkupLine($"[bold]Created:[/]  {job.CreatedAt:u}");
    }
}
