using System.CommandLine;
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
    /// (ConfigHealthCheck) can reuse the exact same set for its read-only assessment.
    /// <para>
    /// GENERATED from the <c>[DoctorCheck(Suite = DoctorSuite.Config)]</c> declarations (#3319), not
    /// hand-written: a check class carrying the attribute is registered by construction, so the
    /// "compiles, has tests, never runs" failure has no way to occur.
    /// </para>
    /// </summary>
    internal static readonly IReadOnlyList<IConfigCheck> Checks = GeneratedDoctorChecks.CreateConfigChecks();

    /// <summary>
    /// Read-only findings the operator should see but which are NEVER auto-applied (issue #2798).
    /// Deliberately a separate list from <see cref="Checks"/>: <see cref="IConfigAdvisory"/> has no
    /// <c>Apply</c>, so nothing here can be wired into the <c>--yes</c> loop even by accident.
    /// <para>
    /// Generated separately for the same reason (#3319). The suite is a DECLARED attribute argument
    /// rather than something inferred from the implemented interface, so the check/advisory split
    /// survives the generator instead of depending on a heuristic getting it right.
    /// </para>
    /// </summary>
    internal static readonly IReadOnlyList<IConfigAdvisory> Advisories = GeneratedDoctorChecks.CreateAdvisories();

    /// <summary>
    /// Renders every applicable advisory. Emits nothing when none apply - an advisory that always
    /// printed would be boilerplate an operator learns to skip, so its presence alone must mean a
    /// finding exists (issue #2798 AC4).
    /// </summary>
    private static void ReportAdvisories(ConfigDocument config)
    {
        foreach (var advisory in Advisories.Where(a => a.IsApplicable(config)))
        {
            AnsiConsole.MarkupLine($"[yellow]![/] [bold]{CliText.SafeDisplay(advisory.Id)}[/]");
            AnsiConsole.MarkupLine($"        {CliText.SafeDisplay(advisory.Describe(config))}");
            AnsiConsole.MarkupLine($"        [dim]Advisory only - not changed automatically:[/] {CliText.SafeDisplay(advisory.Remediation)}\n");
        }
    }

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
        var storePath = ConfigStoreBootstrap.ResolveStorePath(configPath, new System.IO.Abstractions.FileSystem());
        if (!File.Exists(configPath) && !File.Exists(storePath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Config not found at [dim]{CliText.SafeDisplay(configPath)}[/]. Run [green]botnexus init[/] first.");
            return 1;
        }

        AnsiConsole.MarkupLine($"  Checking config at [dim]{CliText.SafeDisplay(configPath)}[/]...\n");

        // Read through the canonical writer so store-only homes and store-wins homes inspect the
        // same authoritative document the gateway uses.
        ConfigDocument document;
        try
        {
            document = await CliConfigMutation.ReadAsync(configPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Unable to load config: {CliText.SafeDisplay(ex.Message)}");
            return 1;
        }

        // Checks are ordered migrations. Determine applicability against the projected document
        // after each earlier applicable fix, so later checks observe canonical paths created by a
        // migration rather than the stale pre-migration shape.
        var applicabilityProjection = ConfigDocument.Parse(document.ToJsonString());
        var applicable = new List<IConfigCheck>();
        foreach (var check in Checks)
        {
            if (!check.IsApplicable(applicabilityProjection))
                continue;

            applicable.Add(check);
            try
            {
                check.Apply(applicabilityProjection);
            }
            catch (InvalidOperationException)
            {
                // Keep the failing check visible. Preview validation below owns the actionable
                // rejection and guarantees the persisted document remains untouched.
                break;
            }
        }

        // Issue #2798 AC4: advisories are REPORTED and never applied, so they are evaluated and
        // rendered separately from the auto-applied checks above. A wildcard bind may be a
        // deliberate operator choice, and AC3 requires an existing config to survive untouched -
        // so the finding must be visible without --yes ever rewriting it.
        ReportAdvisories(document);

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
        var acceptedChecks = new List<IConfigCheck>();
        var alreadyOkCount = Checks.Count - applicable.Count;

        for (var i = 0; i < applicable.Count; i++)
        {
            var check = applicable[i];
            AnsiConsole.MarkupLine($"  [bold]{CliText.SafeDisplay($"[{i + 1}/{applicable.Count}]")}[/] [bold]{CliText.SafeDisplay(check.Id)}[/]");
            AnsiConsole.MarkupLine($"        {CliText.SafeDisplay(check.Description)}");
            AnsiConsole.MarkupLine($"        [dim]Suggested fix:[/] {CliText.SafeDisplay(check.FixDescription)}");

            if (dryRun)
            {
                AnsiConsole.MarkupLine("        [yellow]--dry-run[/]: would apply\n");
                acceptedChecks.Add(check);
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
                acceptedChecks.Add(check);
                appliedCount++;
                AnsiConsole.MarkupLine("        [green]✓ applied[/]\n");
            }
            else
            {
                skippedCount++;
                AnsiConsole.MarkupLine("        [dim]— skipped[/]\n");
            }
        }

        if (acceptedChecks.Count > 0)
        {
            var preview = ConfigDocument.Parse(document.ToJsonString());
            try
            {
                ApplyAcceptedChecks(preview, acceptedChecks);
            }
            catch (InvalidOperationException ex)
            {
                AnsiConsole.MarkupLine("[red]Config validation failed; the existing config was not modified:[/]");
                AnsiConsole.MarkupLine($"  [red]\u2022[/] {CliText.SafeDisplay(ex.Message)}");
                return 1;
            }

            var previewErrors = PlatformConfigLoader.ValidateRawJson(preview.ToJsonString());
            if (previewErrors.Count > 0)
            {
                AnsiConsole.MarkupLine("[red]Config validation failed; the existing config was not modified:[/]");
                foreach (var error in previewErrors)
                    AnsiConsole.MarkupLine($"  [red]\u2022[/] {CliText.SafeDisplay(error)}");
                return 1;
            }
        }

        // Re-run the selected fixes against the authoritative document inside the writer lock and
        // use the validating overload. A concurrent change is therefore preserved and malformed
        // output can never reach either backend.
        if (!dryRun && appliedCount > 0)
        {
            var writer = CliConfigMutation.CreateWriter(configPath);
            var errors = await writer.MutateDocumentValidatedAsync(
                persisted =>
                {
                    try
                    {
                        ApplyAcceptedChecks(persisted, acceptedChecks);
                        return null;
                    }
                    catch (InvalidOperationException ex)
                    {
                        return ex.Message;
                    }
                },
                "doctor-config",
                cancellationToken,
                ["gateway", "agents"]);

            if (errors.Count > 0)
            {
                AnsiConsole.MarkupLine("[red]Config validation failed; the existing config was not modified:[/]");
                foreach (var error in errors)
                    AnsiConsole.MarkupLine($"  [red]\u2022[/] {CliText.SafeDisplay(error)}");
                return 1;
            }
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
            AnsiConsole.MarkupLine($"\n[dim]Config path: {CliText.SafeDisplay(configPath)}[/]");

        return 0;
    }
    internal static void ApplyAcceptedChecks(ConfigDocument document, IEnumerable<IConfigCheck> acceptedChecks)
    {
        foreach (var check in acceptedChecks)
        {
            if (check.IsApplicable(document))
                check.Apply(document);
        }
    }

}

