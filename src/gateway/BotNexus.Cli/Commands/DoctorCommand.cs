using System.CommandLine;
using BotNexus.Cli.Commands.Doctor;
using BotNexus.Domain.World;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// <c>botnexus doctor</c> - CLI diagnostics. Running the bare command executes every registered
/// <see cref="IDoctorCheck"/> in a deterministic order and prints a section per check plus a final
/// healthy/warning/error summary with a script-friendly aggregate exit code (issue #2041). Focused
/// subcommands (<c>doctor locations</c>, <c>doctor config</c>, <c>doctor agents</c>) remain for
/// targeted runs. The read-only reconciliation check runs as part of the aggregate suite; the
/// destructive orphan cleanup is opt-in via <c>--cleanup-orphans</c> (issue #2039) and, in a
/// non-interactive terminal, never deletes without that explicit flag. New checks are added only to
/// <see cref="DoctorCheckRegistry"/>, so the aggregate suite can never silently omit one.
/// </summary>
internal sealed class DoctorCommand
{
    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var command = new Command("doctor", "Run the complete CLI diagnostic suite (or a focused check).");

        // Opt-in destructive reconciliation of orphaned persistent agent workspaces (issue #2039).
        // Absent this flag the bare run stays non-destructive: the read-only check still reports drift.
        var cleanupOption = new Option<bool>(
            "--cleanup-orphans",
            "After the diagnostic suite, reconcile persistent agent workspaces and delete orphaned directories (prompts when interactive).");
        command.AddOption(cleanupOption);

        // Bare `doctor`: run every registered check and return an aggregate exit code.
        command.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var cleanup = context.ParseResult.GetValueForOption(cleanupOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            var exitCode = await RunAggregateAsync(
                DoctorCheckRegistry.CreateDefault(),
                new DoctorCheckContext(configPath, home, verbose),
                CancellationToken.None);

            if (cleanup)
            {
                AnsiConsole.WriteLine();
                var reconcileExit = await ExecuteAgentsAsync(
                    home,
                    cleanupOrphans: true,
                    AnsiConsole.Profile.Capabilities.Interactive,
                    CancellationToken.None);
                exitCode = Math.Max(exitCode, reconcileExit);
            }

            context.ExitCode = exitCode;
        });

        // Dedicated `doctor agents` subcommand for a focused reconciliation run.
        var agentsCleanupOption = new Option<bool>(
            "--cleanup-orphans",
            "Delete orphaned persistent agent workspaces (prompts when interactive, no-op without this flag when non-interactive).");
        var agentsCommand = new Command("agents", "Reconcile persistent agent workspaces with configured agents.")
        {
            agentsCleanupOption
        };
        agentsCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var cleanup = context.ParseResult.GetValueForOption(agentsCleanupOption);
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = await ExecuteAgentsAsync(
                home,
                cleanup,
                AnsiConsole.Profile.Capabilities.Interactive,
                CancellationToken.None);
        });
        command.AddCommand(agentsCommand);

        var locationsCommand = new Command("locations", "Check location accessibility.");
        locationsCommand.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteLocationsAsync(configPath, verbose, CancellationToken.None);
        });

        command.AddCommand(locationsCommand);
        command.AddCommand(new DoctorConfigCommand().Build(verboseOption, targetOption));
        return command;
    }

    /// <summary>
    /// Reconciles the persistent agent workspaces under the resolved agents root against the enabled
    /// agents declared in <c>config.json</c>, prints the plan, and (only when approved) deletes
    /// orphaned directories. Approval comes either from <paramref name="cleanupOrphans"/> (the explicit
    /// opt-in flag) or, in an interactive terminal, from the <paramref name="confirm"/> prompt. In a
    /// non-interactive terminal without the flag the method is strictly non-destructive. Returns 0 when
    /// no orphans remain (nothing found, or all deleted), 1 when orphans remain unresolved (findings),
    /// and 2 for an execution error (missing/unreadable config, unsafe symlink, or a failed deletion) -
    /// distinguishing findings from errors as the issue requires.
    /// </summary>
    /// <param name="home">The resolved BotNexus home whose <c>config.json</c> and agents root are used.</param>
    /// <param name="cleanupOrphans">Explicit opt-in to delete orphaned directories without prompting.</param>
    /// <param name="interactive">Whether the current terminal can prompt the user.</param>
    /// <param name="cancellationToken">Cancellation for config loading.</param>
    /// <param name="confirm">Test/override seam for the interactive confirmation prompt.</param>
    /// <returns>0 = healthy/resolved, 1 = orphans remain, 2 = execution error.</returns>
    internal async Task<int> ExecuteAgentsAsync(
        string home,
        bool cleanupOrphans,
        bool interactive,
        CancellationToken cancellationToken,
        Func<string, bool>? confirm = null)
    {
        var configPath = Path.Combine(home, "config.json");
        var config = await LoadConfigRequiredAsync(configPath, cancellationToken);
        if (config is null)
            return 2;

        var reconciler = new PersistentAgentWorkspaceReconciler();
        var agentsRoot = PersistentAgentWorkspaceReconciler.ResolveAgentsRoot(home, config.Gateway?.AgentsDirectory);
        IReadOnlyList<PersistentAgentWorkspaceEntry> plan;
        try
        {
            plan = reconciler.BuildPlan(agentsRoot, config);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Unable to inspect agent workspaces: {Markup.Escape(ex.Message)}");
            return 2;
        }

        AnsiConsole.MarkupLine($"Agent workspace reconciliation plan for [dim]{Markup.Escape(agentsRoot)}[/]:");
        foreach (var entry in plan)
        {
            var state = entry.IsOrphaned
                ? (entry.IsUnsafeLink ? "[red]unsafe orphan[/]" : "[yellow]orphaned[/]")
                : "[green]registered[/]";
            AnsiConsole.MarkupLine($"  {state}  {Markup.Escape(entry.DirectoryName)}");
        }

        var orphans = plan.Where(entry => entry.IsOrphaned).ToArray();
        if (orphans.Length == 0)
        {
            AnsiConsole.MarkupLine("[green]No orphaned persistent agent workspaces found.[/]");
            return 0;
        }

        if (orphans.Any(entry => entry.IsUnsafeLink))
        {
            AnsiConsole.MarkupLine("[red]Unsafe symlink/reparse-point orphan detected; no directories were deleted.[/]");
            return 2;
        }

        var approved = cleanupOrphans;
        if (!approved && interactive)
        {
            approved = (confirm ?? (message => AnsiConsole.Confirm(message, defaultValue: false)))(
                $"Delete the {orphans.Length} enumerated orphaned workspace(s)?");
        }

        if (!approved)
        {
            AnsiConsole.MarkupLine(interactive
                ? "[yellow]Cleanup declined; orphaned workspaces remain.[/]"
                : "[yellow]Non-interactive mode is non-destructive. Re-run with --cleanup-orphans to delete the listed workspaces.[/]");
            return 1;
        }

        try
        {
            var deleted = reconciler.DeleteOrphans(agentsRoot, orphans);
            AnsiConsole.MarkupLine($"[green]Deleted {deleted} orphaned persistent agent workspace(s).[/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Cleanup failed: {Markup.Escape(ex.Message)}");
            return 2;
        }
    }

    /// <summary>
    /// Runs the supplied checks in order, printing a section per check and a final summary, and returns
    /// a deterministic aggregate exit code: 0 when every check is healthy, 1 when any check reports a
    /// warning, 2 when any check reports an error. Independent checks always run to completion even
    /// after one reports a finding; an unexpected exception from a check is contained and surfaced as
    /// an error section so the remaining checks still run. Exposed for tests to drive with an isolated
    /// check set and an injected non-interactive <see cref="AnsiConsole"/>.
    /// </summary>
    /// <param name="checks">The ordered checks to run (typically <see cref="DoctorCheckRegistry.CreateDefault"/>).</param>
    /// <param name="context">Ambient inputs (config path, home, verbosity) shared by every check.</param>
    /// <param name="cancellationToken">Cancellation for the whole suite.</param>
    /// <returns>0 = all healthy, 1 = at least one warning, 2 = at least one error.</returns>
    internal static async Task<int> RunAggregateAsync(
        IReadOnlyList<IDoctorCheck> checks,
        DoctorCheckContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(context);

        AnsiConsole.MarkupLine($"[bold]BotNexus doctor[/] - running {checks.Count} check(s)\n");

        var healthy = 0;
        var warning = 0;
        var error = 0;

        foreach (var check in checks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DoctorCheckResult result;
            try
            {
                result = await check.RunAsync(context, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Contain an unexpected failure so the remaining independent checks still run.
                result = DoctorCheckResult.Error($"check '{check.Id}' threw unexpectedly", ex.Message);
            }

            switch (result.Outcome)
            {
                case DoctorOutcome.Healthy:
                    healthy++;
                    break;
                case DoctorOutcome.Warning:
                    warning++;
                    break;
                default:
                    error++;
                    break;
            }

            RenderSection(check, result);
        }

        AnsiConsole.WriteLine();
        var summaryColor = error > 0 ? "red" : warning > 0 ? "yellow" : "green";
        var summaryIcon = error > 0 ? "x" : warning > 0 ? "!" : "\u221a";
        AnsiConsole.Write(new Rule(
            $"[{summaryColor}]{summaryIcon}[/] [green]{healthy} healthy[/]  [yellow]{warning} warning[/]  [red]{error} error[/]")
        { Justification = Justify.Left });

        // Deterministic aggregate exit code: error dominates warning dominates healthy.
        return error > 0 ? 2 : warning > 0 ? 1 : 0;
    }

    private static void RenderSection(IDoctorCheck check, DoctorCheckResult result)
    {
        var (icon, color) = result.Outcome switch
        {
            DoctorOutcome.Healthy => ("\u221a", "green"),
            DoctorOutcome.Warning => ("!", "yellow"),
            _ => ("x", "red")
        };

        AnsiConsole.MarkupLine(
            $"[{color}]{icon}[/] [bold]{Markup.Escape(check.Title)}[/] - {Markup.Escape(result.Summary)}");
        foreach (var detail in result.Details)
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(detail)}[/]");
    }

    public async Task<int> ExecuteLocationsAsync(bool verbose, CancellationToken cancellationToken)
        => await ExecuteLocationsAsync(PlatformConfigLoader.DefaultConfigPath, verbose, cancellationToken);

    public async Task<int> ExecuteLocationsAsync(string configPath, bool verbose, CancellationToken cancellationToken)
    {
        var config = await LoadConfigRequiredAsync(configPath, cancellationToken);
        if (config is null)
            return 1;

        var locations = WorldDescriptorBuilder.Build(config, null, null)
            .Locations
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (locations.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No locations registered.[/]");
            return 0;
        }

        var interactive = AnsiConsole.Profile.Capabilities.Interactive;

        if (interactive)
            AnsiConsole.MarkupLine($"[dim]Checking {locations.Length} location(s)...[/]");
        else
            AnsiConsole.MarkupLine($"Checking [green]{locations.Length}[/] locations...\n");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var healthyCount = 0;
        var warningCount = 0;
        var errorCount = 0;

        var table = new Table()
            .Border(interactive ? TableBorder.Rounded : TableBorder.Simple)
            .AddColumn("Status")
            .AddColumn("Location")
            .AddColumn("Target")
            .AddColumn("Message");

        void Accumulate(LocationHealthResult result, Location location)
        {
            var icon = result.Status switch
            {
                LocationHealthStatus.Healthy => "[green]\u221a[/]",
                LocationHealthStatus.Warning => "[yellow]![/]",
                _ => "[red]x[/]"
            };
            healthyCount += result.Status == LocationHealthStatus.Healthy ? 1 : 0;
            warningCount += result.Status == LocationHealthStatus.Warning ? 1 : 0;
            errorCount += result.Status == LocationHealthStatus.Error ? 1 : 0;
            table.AddRow(icon, Markup.Escape(location.Name), Markup.Escape(result.Target), Markup.Escape(result.Message));
        }

        if (interactive)
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync("Checking locations...", async ctx =>
                {
                    foreach (var location in locations)
                    {
                        ctx.Status($"Checking [dim]{Markup.Escape(location.Name)}[/]...");
                        Accumulate(await LocationProbe.CheckLocationAsync(location, httpClient, cancellationToken), location);
                    }
                });
        }
        else
        {
            foreach (var location in locations)
                Accumulate(await LocationProbe.CheckLocationAsync(location, httpClient, cancellationToken), location);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        var summaryColor = errorCount > 0 ? "red" : warningCount > 0 ? "yellow" : "green";
        var summaryIcon = errorCount > 0 ? "x" : warningCount > 0 ? "!" : "\u221a";
        AnsiConsole.Write(new Rule(
            $"[{summaryColor}]{summaryIcon}[/] [green]{healthyCount} healthy[/]  [yellow]{warningCount} warning[/]  [red]{errorCount} error[/]")
        { Justification = Justify.Left });

        if (verbose)
            AnsiConsole.MarkupLine($"[dim]Loaded from: {Markup.Escape(configPath)}[/]");

        return errorCount == 0 ? 0 : 1;
    }

    private static async Task<PlatformConfig?> LoadConfigRequiredAsync(string configPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Config file not found at [dim]{Markup.Escape(configPath)}[/]. Run [green]botnexus init[/] first.");
            return null;
        }

        try
        {
            return await PlatformConfigLoader.LoadAsync(configPath, cancellationToken, validateOnLoad: false);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Unable to load config: {Markup.Escape(ex.Message)}");
            return null;
        }
    }
}
