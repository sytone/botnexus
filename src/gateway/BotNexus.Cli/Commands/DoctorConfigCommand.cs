using System.CommandLine;
using System.Text.Json.Nodes;
using BotNexus.Cli.Commands.Doctor;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// Implements <c>botnexus doctor config</c> — guided config migration.
/// Compares the existing config.json against registered <see cref="IConfigCheck"/> implementations,
/// reports gaps, and optionally applies fixes via <see cref="PlatformConfigWriter"/>.
/// </summary>
internal sealed class DoctorConfigCommand
{
    /// <summary>All registered checks, evaluated in order. Internal so the aggregate doctor suite
    /// (ConfigHealthCheck) can reuse the exact same set for its read-only assessment.</summary>
    internal static readonly IReadOnlyList<IConfigCheck> Checks =
    [
        new ExtensionsBlockCheck(),
        new SkillsWorldDefaultCheck(),
        new CronCheck(),
        new MemoryAgentDefaultCheck(),
        new CompactionModelCheck(),
        new CompactionModelMissingCheck(),
        new DevOriginEnforcementCheck(),
    ];

    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var yesOption = new Option<bool>("--yes", "Apply all applicable fixes without prompting.");
        var dryRunOption = new Option<bool>("--dry-run", "Report what would change but do not write anything.");

        var command = new Command("config", "Guided config migration — detect and apply missing settings.")
        {
            yesOption,
            dryRunOption
        };

        command.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var yes = context.ParseResult.GetValueForOption(yesOption);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteAsync(configPath, yes, dryRun, verbose, CancellationToken.None);
        });

        return command;
    }

    public async Task<int> ExecuteAsync(
        string configPath,
        bool autoApply,
        bool dryRun,
        bool verbose,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Config not found at [dim]{Markup.Escape(configPath)}[/]. Run [green]botnexus init[/] first.");
            return 1;
        }

        PlatformConfig config;
        try
        {
            config = await PlatformConfigLoader.LoadAsync(configPath, cancellationToken, validateOnLoad: false);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Unable to load config: {Markup.Escape(ex.Message)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"  Checking config at [dim]{Markup.Escape(configPath)}[/]...\n");

        // Load raw JSON so checks operate on the actual persisted nodes
        var rawJson = await File.ReadAllTextAsync(configPath, cancellationToken);
        var root = JsonNode.Parse(rawJson)?.AsObject() ?? new JsonObject();

        var applicable = Checks.Where(c => c.IsApplicable(root)).ToList();

        if (applicable.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]✓[/] Config is up to date — no changes needed.");
            return 0;
        }

        // Only prompt when a real interactive stdin is attached. Checking
        // AnsiConsole.Profile.Capabilities.Interactive alone is not sufficient:
        // under `dotnet test` (and other automation) a terminal may be reported
        // as interactive while stdin is unavailable, so AnsiConsole.Confirm would
        // block forever waiting on input that never arrives. Guarding with
        // Console.IsInputRedirected keeps the CLI from hanging in non-tty contexts.
        var canPrompt = AnsiConsole.Profile.Capabilities.Interactive
            && !Console.IsInputRedirected
            && !autoApply;
        var appliedCount = 0;
        var skippedCount = 0;
        var alreadyOkCount = Checks.Count - applicable.Count;

        for (var i = 0; i < applicable.Count; i++)
        {
            var check = applicable[i];
            AnsiConsole.MarkupLine($"  [bold]{Markup.Escape($"[{i + 1}/{applicable.Count}]")}[/] [bold]{Markup.Escape(check.Id)}[/]");
            AnsiConsole.MarkupLine($"        {Markup.Escape(check.Description)}");
            AnsiConsole.MarkupLine($"        [dim]Suggested fix:[/] {Markup.Escape(check.FixDescription)}");

            if (dryRun)
            {
                AnsiConsole.MarkupLine("        [yellow]--dry-run[/]: would apply\n");
                appliedCount++;
                continue;
            }

            bool apply;
            if (autoApply)
            {
                apply = true;
                AnsiConsole.MarkupLine("        [dim]--yes: applying...[/]");
            }
            else if (canPrompt)
            {
                apply = AnsiConsole.Confirm("        Apply?", defaultValue: true);
            }
            else
            {
                // No interactive stdin and --yes was not passed: never block on a
                // prompt. Skip the fix and hint at the non-interactive flag.
                apply = false;
                AnsiConsole.MarkupLine("        [dim]— skipped (no interactive input; re-run with [green]--yes[/] to apply)[/]");
                skippedCount++;
                continue;
            }

            if (apply)
            {
                check.Apply(root);
                appliedCount++;
                AnsiConsole.MarkupLine("        [green]✓ applied[/]\n");
            }
            else
            {
                skippedCount++;
                AnsiConsole.MarkupLine("        [dim]— skipped[/]\n");
            }
        }

        // Write back if anything was applied (and not dry-run)
        if (!dryRun && appliedCount > 0)
        {
            var fileSystem = new System.IO.Abstractions.FileSystem();
            var backupsDir = Path.Combine(Path.GetDirectoryName(configPath) ?? PlatformConfigLoader.DefaultHomePath, "backups");
            var writer = new PlatformConfigWriter(configPath, fileSystem, new ConfigBackupService(backupsDir, fileSystem));
            var updatedJson = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await writer.MutateAsync(rootNode =>
            {
                var replacement = JsonNode.Parse(updatedJson)?.AsObject() ?? new JsonObject();
                rootNode.Clear();
                foreach (var kvp in replacement)
                    rootNode[kvp.Key] = kvp.Value?.DeepClone();
            }, "doctor-config", cancellationToken);
        }

        AnsiConsole.WriteLine();
        var dryRunNote = dryRun ? " [dim](dry-run — nothing written)[/]" : string.Empty;
        AnsiConsole.Write(new Rule(
            $"[green]{appliedCount} fix{(appliedCount == 1 ? "" : "es")} applied[/]  " +
            $"[yellow]{skippedCount} skipped[/]  " +
            $"[dim]{alreadyOkCount} already correct[/]" +
            dryRunNote)
        { Justification = Justify.Left });

        if (verbose)
            AnsiConsole.MarkupLine($"\n[dim]Config path: {Markup.Escape(configPath)}[/]");

        return 0;
    }
}
