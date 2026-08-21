using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Configuration;
using Spectre.Console;
using BotNexus.Cli.Services;

namespace BotNexus.Cli.Commands;

internal sealed class ConfigCommands(IConfigPathResolver configPathResolver)
{
    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var command = new Command("config", "Read and update BotNexus configuration.");

        var keyArgument = new Argument<string>("key", "Dotted config key path (example: gateway.listenUrl).");
        var getCommand = new Command("get", "Get a config value by dotted key.")
        {
            keyArgument
        };
        getCommand.SetHandler(async context =>
        {
            var key = context.ParseResult.GetValueForArgument(keyArgument);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteGetAsync(key, configPath, verbose, CancellationToken.None);
        });

        var valueArgument = new Argument<string>("value", "Value to set.");
        var setCommand = new Command("set", "Set a config value by dotted key.")
        {
            keyArgument,
            valueArgument
        };
        setCommand.SetHandler(async context =>
        {
            var key = context.ParseResult.GetValueForArgument(keyArgument);
            var value = context.ParseResult.GetValueForArgument(valueArgument);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteSetAsync(key, value, configPath, verbose, CancellationToken.None);
        });

        var schemaOutputOption = new Option<string>("--output", () => "docs\\botnexus-config.schema.json", "Schema output path.");
        var schemaCommand = new Command("schema", "Generate JSON schema for platform config.")
        {
            schemaOutputOption
        };
        schemaCommand.SetHandler(async context =>
        {
            var outputPath = context.ParseResult.GetValueForOption(schemaOutputOption) ?? "docs\\botnexus-config.schema.json";
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteSchemaAsync(outputPath, verbose, CancellationToken.None);
        });

        command.AddCommand(getCommand);
        command.AddCommand(setCommand);
        command.AddCommand(schemaCommand);
        command.AddCommand(BuildBackupsCommand(verboseOption, targetOption));
        command.AddCommand(BuildRestoreCommand(verboseOption, targetOption));
        return command;
    }

    /// <summary>
    /// <c>botnexus config backups list</c> (#2884): the read half of the restore story. Backups
    /// were write-only before this - 50 files an operator could see but had no supported way to
    /// evaluate or use.
    /// </summary>
    private Command BuildBackupsCommand(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var backupsCommand = new Command("backups", "Inspect retained config.json backups.");
        var listCommand = new Command("list", "List retained config.json backups with a validity verdict.");
        listCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var configPath = Path.Combine(CliPaths.ResolveTarget(target), "config.json");
            context.ExitCode = await Task.FromResult(ExecuteBackupsList(configPath));
        });

        backupsCommand.AddCommand(listCommand);
        return backupsCommand;
    }

    /// <summary>
    /// <c>botnexus config restore &lt;id&gt;</c> (#2884). Dry-run unless <c>--commit</c> is passed.
    /// </summary>
    /// <remarks>
    /// The commit flag is opt-in rather than a <c>--dry-run</c> opt-out because this is the one
    /// config command whose stated purpose is to discard the current document. An operator who
    /// mistypes an id under an opt-out design overwrites their config; under this design they get a
    /// preview.
    /// </remarks>
    private Command BuildRestoreCommand(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var idArgument = new Argument<string>("id", "Backup id from 'botnexus config backups list'.");
        var commitOption = new Option<bool>("--commit", "Actually perform the restore. Without this the restore is previewed and nothing is written.");
        var restoreCommand = new Command("restore", "Validate and restore a config.json backup.")
        {
            idArgument,
            commitOption
        };
        restoreCommand.SetHandler(async context =>
        {
            var id = context.ParseResult.GetValueForArgument(idArgument);
            var commit = context.ParseResult.GetValueForOption(commitOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var configPath = Path.Combine(CliPaths.ResolveTarget(target), "config.json");
            context.ExitCode = await ExecuteRestoreAsync(id, configPath, commit, verbose, CancellationToken.None);
        });

        return restoreCommand;
    }

    /// <summary>
    /// Renders every retained backup with its timestamp, trigger reason, size and load verdict
    /// (#2884 AC1). Exits 0 even when the directory is empty: "no backups yet" is a normal state,
    /// not a command failure.
    /// </summary>
    public int ExecuteBackupsList(string configPath)
    {
        var service = CliConfigMutation.CreateRestoreService(configPath);
        var inspections = service.ListWithVerdicts();

        if (inspections.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No config backups found.[/]");
            return 0;
        }

        var table = new Table();
        table.AddColumn("Id");
        table.AddColumn("Timestamp");
        table.AddColumn("Reason");
        table.AddColumn("Size");
        table.AddColumn("Verdict");

        foreach (var inspection in inspections)
        {
            var backup = inspection.Backup;
            table.AddRow(
                CliText.SafeDisplay(backup.Id),
                CliText.SafeDisplay(backup.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown"),
                CliText.SafeDisplay(string.IsNullOrEmpty(backup.Reason) ? "unknown" : backup.Reason),
                $"{backup.SizeBytes} B",
                DescribeVerdict(inspection.Verdict));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    /// <summary>
    /// Validates and (with <paramref name="commit"/>) performs a restore (#2884 AC2-AC5).
    /// </summary>
    /// <returns>0 when the restore succeeded or was previewed; 1 when it was refused.</returns>
    public async Task<int> ExecuteRestoreAsync(
        string id,
        string configPath,
        bool commit,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var service = CliConfigMutation.CreateRestoreService(configPath);

        ConfigRestoreResult result;
        try
        {
            result = await service.RestoreAsync(id, commit, cancellationToken);
        }
        catch (PlatformConfigLockTimeoutException ex)
        {
            // Same posture as every other CLI write path (#2134): another process holds the config
            // critical section, so refuse rather than writing without the lock.
            AnsiConsole.MarkupLine($"[red]Error:[/] {CliText.SafeDisplay(ex.Message)}");
            return 1;
        }

        if (result.Errors.Count > 0)
        {
            AnsiConsole.MarkupLine("[red]Restore refused; the existing config was not modified:[/]");
            foreach (var error in result.Errors)
                AnsiConsole.MarkupLine($"  [red]\u2022[/] {CliText.SafeDisplay(error)}");
            return 1;
        }

        if (result.DryRun)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Dry run:[/] backup [green]{CliText.SafeDisplay(id)}[/] is {DescribeVerdict(result.Verdict)} and would be restored. "
                + "Nothing was written. Re-run with [green]--commit[/] to perform the restore.");
            return 0;
        }

        AnsiConsole.MarkupLine($"[green]\u2713[/] Restored config from backup [green]{CliText.SafeDisplay(id)}[/].");
        if (verbose)
            AnsiConsole.MarkupLine($"[dim]Config: {CliText.SafeDisplay(configPath)}[/]");

        return 0;
    }

    private static string DescribeVerdict(ConfigBackupVerdict verdict) => verdict switch
    {
        ConfigBackupVerdict.Valid => "[green]valid[/]",
        ConfigBackupVerdict.NeedsMigration => "[yellow]needs-migration[/]",
        _ => "[red]unloadable[/]",
    };

    public async Task<int> ExecuteGetAsync(string keyPath, bool verbose, CancellationToken cancellationToken)
        => await ExecuteGetAsync(keyPath, PlatformConfigLoader.DefaultConfigPath, verbose, cancellationToken);

    public async Task<int> ExecuteGetAsync(string keyPath, string configPath, bool verbose, CancellationToken cancellationToken)
    {
        var config = await LoadConfigRequiredAsync(configPath, cancellationToken);
        if (config is null)
            return 1;

        if (!configPathResolver.TryGetValue(config, keyPath, out var value, out var error))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {CliText.SafeDisplay(error)}");
            return 1;
        }

        PrintValue(value);
        if (verbose)
            AnsiConsole.MarkupLine($"[dim]Read key: {CliText.SafeDisplay(keyPath)}[/]");

        return 0;
    }

    public async Task<int> ExecuteSetAsync(string keyPath, string rawValue, bool verbose, CancellationToken cancellationToken)
        => await ExecuteSetAsync(keyPath, rawValue, PlatformConfigLoader.DefaultConfigPath, verbose, cancellationToken);

    public async Task<int> ExecuteSetAsync(string keyPath, string rawValue, string configPath, bool verbose, CancellationToken cancellationToken)
    {
        var config = await LoadConfigRequiredAsync(configPath, cancellationToken);
        if (config is null)
            return 1;

        // The typed graph is used only to type-check and coerce the incoming string against the
        // declared shape of the key. It is deliberately NOT written back: re-serializing the typed
        // object would drop everything the graph does not model (#2057).
        if (!configPathResolver.TrySetValue(config, keyPath, rawValue, out var error))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {CliText.SafeDisplay(error)}");
            return 1;
        }

        if (!configPathResolver.TryGetValue(config, keyPath, out var coerced, out var readBackError))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {CliText.SafeDisplay(readBackError)}");
            return 1;
        }

        var saveCode = await CliConfigMutation.ApplyAsync(
            configPath,
            document => document.TrySetFrom(keyPath, coerced, out var setError) ? null : setError,
            "before-config-set",
            verbose,
            cancellationToken);
        if (saveCode != 0)
            return saveCode;

        AnsiConsole.MarkupLine($"[green]\u2713[/] Set [green]{CliText.SafeDisplay(keyPath)}[/].");
        return 0;
    }

    public Task<int> ExecuteSchemaAsync(string outputPath, bool verbose, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Output path is required.");
            return Task.FromResult(1);
        }

        var resolvedPath = Path.GetFullPath(outputPath);
        PlatformConfigSchema.WriteSchema(resolvedPath);

        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(1);

        AnsiConsole.MarkupLine($"[green]\u2713[/] Generated schema: [dim]{CliText.SafeDisplay(resolvedPath)}[/]");
        if (verbose)
        {
            var availablePaths = configPathResolver.GetAvailablePaths(new PlatformConfig());
            AnsiConsole.MarkupLine($"[dim]Schema generated from model graph ({availablePaths.Count} discoverable config paths).[/]");
        }

        return Task.FromResult(0);
    }

    private static async Task<PlatformConfig?> LoadConfigRequiredAsync(CancellationToken cancellationToken)
        => await LoadConfigRequiredAsync(PlatformConfigLoader.DefaultConfigPath, cancellationToken);

    private static async Task<PlatformConfig?> LoadConfigRequiredAsync(string configPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Config file not found at [dim]{CliText.SafeDisplay(configPath)}[/]. Run [green]botnexus init[/] first.");
            return null;
        }

        try
        {
            return PlatformConfigAccessor.Shared.Get(configPath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Unable to load config: {CliText.SafeDisplay(ex.Message)}");
            return null;
        }
    }

    private static void PrintValue(object? value)
    {
        if (value is null)
        {
            AnsiConsole.WriteLine("null");
            return;
        }

        if (value is string stringValue)
        {
            AnsiConsole.WriteLine(stringValue);
            return;
        }

        AnsiConsole.WriteLine(JsonSerializer.Serialize(value, CreateWriteJsonOptions()));
    }

    private static JsonSerializerOptions CreateWriteJsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

