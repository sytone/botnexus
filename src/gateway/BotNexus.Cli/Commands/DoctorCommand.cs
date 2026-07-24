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
/// subcommands (<c>doctor locations</c>, <c>doctor config</c>) remain for targeted runs. New checks
/// are added only to <see cref="DoctorCheckRegistry"/>, so the aggregate suite can never silently
/// omit one.
/// </summary>
internal sealed class DoctorCommand
{
    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var command = new Command("doctor", "Run the complete CLI diagnostic suite (or a focused check).");

        // Bare `doctor`: run every registered check and return an aggregate exit code.
        command.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await RunAggregateAsync(
                DoctorCheckRegistry.CreateDefault(),
                new DoctorCheckContext(configPath, home, verbose),
                CancellationToken.None);
        });

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
