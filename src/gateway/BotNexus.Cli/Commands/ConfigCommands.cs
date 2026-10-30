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

        // Path.Combine, not a backslash literal: off Windows the literal is not a separator, so
        // the documented bare `botnexus config schema` wrote a file actually *named*
        // "docs\botnexus-config.schema.json" into the working directory - one file with a
        // backslash in its name, and no schema where anyone would look for it.
        var defaultSchemaOutput = Path.Combine("docs", "botnexus-config.schema.json");
        var schemaOutputOption = new Option<string>("--output", () => defaultSchemaOutput, "Schema output path.");
        var schemaCommand = new Command("schema", "Generate JSON schema for platform config.")
        {
            schemaOutputOption
        };
        schemaCommand.SetHandler(async context =>
        {
            var outputPath = context.ParseResult.GetValueForOption(schemaOutputOption) ?? defaultSchemaOutput;
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteSchemaAsync(outputPath, verbose, CancellationToken.None);
        });

        command.AddCommand(getCommand);
        command.AddCommand(setCommand);
        command.AddCommand(schemaCommand);
        command.AddCommand(BuildBackupsCommand(verboseOption, targetOption));
        command.AddCommand(BuildRestoreCommand(verboseOption, targetOption));
        command.AddCommand(BuildStoreCommand(targetOption));
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
    /// <c>botnexus config store</c> (#3514): create, inspect and remove the SQLite configuration
    /// store.
    /// </summary>
    /// <remarks>
    /// The store had no writer at all - <c>SqliteConfigStore.WriteDocumentAsync</c> was called only
    /// by the shadow migration deleted in #3510 - so <c>config.db</c> never appeared, and the
    /// provider registration is gated on it existing. The store was unreachable by any supported
    /// action. This command is that action.
    /// </remarks>
    private static Command BuildStoreCommand(Option<string?> targetOption)
    {
        var storeCommand = new Command("store", "Manage the SQLite configuration store.");
        static string ResolveConfigPath(string? target) => Path.Combine(CliPaths.ResolveTarget(target), "config.json");

        var enableCommand = new Command(
            "enable",
            "Create config.db from the current config.json. The store then serves configuration, with its values winning over the file.");
        enableCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var configPath = ResolveConfigPath(target);
            context.ExitCode = await ExecuteStoreEnableAsync(configPath, CancellationToken.None);
        });

        var statusCommand = new Command("status", "Report whether the configuration store exists and how many entries it holds.");
        statusCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var configPath = ResolveConfigPath(target);
            context.ExitCode = await ExecuteStoreStatusAsync(configPath, CancellationToken.None);
        });

        var disableCommand = new Command(
            "disable",
            "Delete config.db. The gateway returns to file-only configuration on the next start.");
        disableCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var configPath = ResolveConfigPath(target);
            context.ExitCode = ExecuteStoreDisable(configPath);
        });

        var parityCommand = new Command("parity", "Compare complete SQL and compatibility documents without changing configuration. Reports counts only, never keys or values.");
        parityCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var configPath = ResolveConfigPath(target);
            context.ExitCode = await ExecuteStoreParityAsync(configPath, CancellationToken.None);
        });

        var applyOption = new Option<bool>("--apply", "Persist the validated complete union. Without this option, only preview.");
        var reconcileCommand = new Command("reconcile", "Preview a complete SQL-first union with compatibility-only fields preserved; use --apply to persist.")
        {
            applyOption
        };
        reconcileCommand.SetHandler(async context =>
        {
            var configPath = ResolveConfigPath(context.ParseResult.GetValueForOption(targetOption));
            context.ExitCode = await ExecuteStoreReconcileAsync(configPath,
                context.ParseResult.GetValueForOption(applyOption), CancellationToken.None);
        });

        storeCommand.AddCommand(reconcileCommand);
        storeCommand.AddCommand(enableCommand);
        storeCommand.AddCommand(statusCommand);
        storeCommand.AddCommand(disableCommand);
        storeCommand.AddCommand(parityCommand);
        return storeCommand;
    }

    /// <summary>
    /// Previews reconciliation by default; only explicit apply constructs the canonical writer.
    /// Diagnostics omit configuration data and exception payloads, including in verbose mode.
    /// Preview permits SQLite WAL/SHM coordination, not configuration/schema/revision/permission/migration writes.
    /// </summary>
    public static async Task<int> ExecuteStoreReconcileAsync(string configPath, bool apply, CancellationToken cancellationToken)
    {
        var preview = await PlatformConfigWriter.PreviewReconciliationAsync(configPath, cancellationToken);
        if (!preview.CanApply)
        {
            AnsiConsole.WriteLine("Reconciliation: refused");
            AnsiConsole.WriteLine(preview.Outcome);
            return 1;
        }
        if (!apply)
        {
            AnsiConsole.WriteLine("Reconciliation: preview");
            AnsiConsole.WriteLine("Complete SQL-first union validated. No configuration was changed; use --apply to persist.");
            AnsiConsole.WriteLine("SQLite may update WAL/SHM coordination files; preview does not change configuration values, schema, revision, permissions or migration state.");
            return 0;
        }
        try
        {
            var writer = BotNexus.Gateway.Configuration.Writers.ConfigWriterFactory.Create(configPath, new System.IO.Abstractions.FileSystem());
            var errors = await writer.ApplyReconciliationAsync(preview, cancellationToken);
            if (errors.Count > 0)
            {
                AnsiConsole.WriteLine("Reconciliation: refused");
                AnsiConsole.WriteLine("State changed or safety validation failed. No configuration was written; preview again.");
                return 1;
            }
            AnsiConsole.WriteLine("Reconciliation: applied");
            AnsiConsole.WriteLine("The complete union was persisted to both backends. Keys and values are omitted.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.WriteLine("Reconciliation: interrupted");
            AnsiConsole.WriteLine("Persistence may be partial. Inspect parity before retrying; no cross-store atomicity is guaranteed.");
            throw;
        }
        catch (Exception)
        {
            AnsiConsole.WriteLine("Reconciliation: failed");
            AnsiConsole.WriteLine("Persistence may be partial. Inspect parity before retrying; no cross-store atomicity is guaranteed.");
            return 1;
        }
    }

    /// <summary>
    /// Explicit read-only compatibility diagnostic. Exit codes: 0 clean, 2 drift, 3 not compared,
    /// 1 unreadable. No output path, key, value, or exception payload is emitted, even in verbose mode.
    /// SQLite WAL/SHM bookkeeping is permitted, never application configuration mutations.
    /// </summary>
    public static async Task<int> ExecuteStoreParityAsync(string configPath, CancellationToken cancellationToken)
    {
        var report = await ConfigStoreParityDiagnostic.InspectAsync(configPath, cancellationToken);
        var status = report.Status switch
        {
            ConfigStoreParityStatus.Clean => "clean",
            ConfigStoreParityStatus.Drift => "drift",
            ConfigStoreParityStatus.Disabled => "disabled",
            ConfigStoreParityStatus.Absent => "absent",
            ConfigStoreParityStatus.SqlOnly => "sql-only",
            _ => "unreadable"
        };
        AnsiConsole.WriteLine($"Parity: {status}");
        AnsiConsole.WriteLine($"SQL: {report.SqlBackend.ToString().ToLowerInvariant()}");
        AnsiConsole.WriteLine($"Compatibility: {report.CompatibilityBackend.ToString().ToLowerInvariant()}");
        if (report.Status is ConfigStoreParityStatus.Clean or ConfigStoreParityStatus.Drift)
        {
            AnsiConsole.WriteLine($"SQL-only: {report.SqlOnlyCount}");
            AnsiConsole.WriteLine($"Compatibility-only: {report.CompatibilityOnlyCount}");
            AnsiConsole.WriteLine($"Changed: {report.ChangedCount}");
        }
        else
        {
            AnsiConsole.WriteLine("Documents not compared; parity is not established.");
        }
        AnsiConsole.WriteLine("Read-only inspection; no configuration was changed. Keys and values are omitted.");
        AnsiConsole.WriteLine("SQLite may update WAL/SHM coordination files; configuration values, schema, revision, permissions and migration state are unchanged.");
        return report.Status switch
        {
            ConfigStoreParityStatus.Clean => 0,
            ConfigStoreParityStatus.Drift => 2,
            ConfigStoreParityStatus.Unreadable => 1,
            _ => 3
        };
    }

    /// <summary>
    /// Creates <c>config.db</c> and populates it from the current <c>config.json</c>.
    /// </summary>
    /// <remarks>
    /// Reads the RAW document rather than a bound <see cref="PlatformConfig"/>: binding collapses
    /// "key absent" and "key present and null" into the same null field, and the store records those
    /// distinctly by design. Populating from a bound object would silently rewrite every deliberate
    /// null as an absence.
    /// </remarks>
    public static async Task<int> ExecuteStoreEnableAsync(string configPath, CancellationToken cancellationToken)
    {
        var fileSystem = new System.IO.Abstractions.FileSystem();

        if (!fileSystem.File.Exists(configPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Config file not found at [dim]{CliText.SafeDisplay(configPath)}[/]. Run [green]botnexus init[/] first.");
            return 1;
        }

        var storePath = ConfigStoreBootstrap.ResolveStorePath(configPath, fileSystem);

        try
        {
            var raw = await fileSystem.File.ReadAllTextAsync(configPath, cancellationToken);
            var document = System.Text.Json.Nodes.JsonNode.Parse(raw)?.AsObject();
            if (document is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] config.json is not a JSON object.");
                return 1;
            }

            await ConfigStoreBootstrap.PopulateAsync(storePath, document, cancellationToken);

            var count = await ConfigStoreBootstrap.CountEntriesAsync(storePath, fileSystem, cancellationToken);
            AnsiConsole.MarkupLine($"[green]Configuration store enabled.[/] [dim]{CliText.SafeDisplay(storePath)}[/]");
            AnsiConsole.MarkupLine($"  {count ?? 0} entries imported from config.json.");
            AnsiConsole.MarkupLine("  Restart the gateway for the store to take effect.");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Unable to create the configuration store: {CliText.SafeDisplay(ex.Message)}");
            return 1;
        }
    }

    /// <summary>Reports whether the store exists and how many entries it holds.</summary>
    public static async Task<int> ExecuteStoreStatusAsync(string configPath, CancellationToken cancellationToken)
    {
        var fileSystem = new System.IO.Abstractions.FileSystem();
        var storePath = ConfigStoreBootstrap.ResolveStorePath(configPath, fileSystem);

        try
        {
            var count = await ConfigStoreBootstrap.CountEntriesAsync(storePath, fileSystem, cancellationToken);

            if (count is null)
            {
                AnsiConsole.MarkupLine("[yellow]Configuration store not enabled.[/] Configuration comes from config.json only.");
                AnsiConsole.MarkupLine("  Enable it with [green]botnexus config store enable[/].");
                return 0;
            }

            AnsiConsole.MarkupLine($"[green]Configuration store enabled.[/] [dim]{CliText.SafeDisplay(storePath)}[/]");
            AnsiConsole.MarkupLine($"  {count} entries. Store values win over config.json.");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Unable to read the configuration store: {CliText.SafeDisplay(ex.Message)}");
            return 1;
        }
    }

    /// <summary>Deletes the store, returning the gateway to file-only configuration.</summary>
    /// <remarks>
    /// No confirmation prompt: the store is a derived copy of <c>config.json</c>, which is untouched,
    /// so this discards nothing that cannot be regenerated by <c>config store enable</c>. That is
    /// deliberately unlike <c>config restore</c>, which overwrites the source document and therefore
    /// requires <c>--commit</c>.
    /// </remarks>
    public static int ExecuteStoreDisable(string configPath)
    {
        var fileSystem = new System.IO.Abstractions.FileSystem();
        var storePath = ConfigStoreBootstrap.ResolveStorePath(configPath, fileSystem);

        if (!fileSystem.File.Exists(storePath))
        {
            AnsiConsole.MarkupLine("[yellow]Configuration store is not enabled.[/] Nothing to do.");
            return 0;
        }

        try
        {
            // Release pooled handles before deleting: a connection pooled from an earlier read keeps
            // the file open, and the delete would fail with "used by another process".
            ConfigStoreBootstrap.ReleaseConnections(storePath);
            fileSystem.File.Delete(storePath);
            AnsiConsole.MarkupLine("[green]Configuration store disabled.[/] config.json serves configuration from the next start.");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Unable to delete the configuration store: {CliText.SafeDisplay(ex.Message)}");
            return 1;
        }
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

        // Shape recognition is not agent existence. Check before the typed resolver can
        // materialize dictionary entries, then recheck against the locked latest document.
        // Unrecognised paths retain the resolver's existing property/type diagnostics.
        var agentIds = ConfigPathBinding.TryRecognise(keyPath, out _)
            ? GetTargetAgentIds(keyPath, rawValue)
            : [];
        if (agentIds.Count > 0)
        {
            var document = await CliConfigMutation.ReadAsync(configPath, cancellationToken);
            if (CheckAgentExistence(document, keyPath, agentIds) is { } existenceError)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {CliText.SafeDisplay(existenceError)}");
                return 1;
            }
        }

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
            document => CheckAgentExistence(document, keyPath, agentIds)
                ?? (document.TrySetFrom(keyPath, coerced, out var setError) ? null : setError),
            "before-config-set",
            verbose,
            cancellationToken);
        if (saveCode != 0)
            return saveCode;

        AnsiConsole.MarkupLine($"[green]\u2713[/] Set [green]{CliText.SafeDisplay(keyPath)}[/].");
        saveCode.PrintReceipt();
        return 0;
    }

    private static IReadOnlyList<string> GetTargetAgentIds(string keyPath, string rawValue)
    {
        // Recognition above has already validated the path. Only the first two dictionary
        // segments matter; settings beneath the agent (including opaque JSON) remain open.
        var segments = keyPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || !segments[0].Equals("agents", StringComparison.OrdinalIgnoreCase))
            return [];
        if (segments.Length > 1)
            return [segments[1]];

        // Whole-collection replacement must not be an alternate agent creation command.
        // Invalid JSON/value kinds are left to the existing typed coercion diagnostics.
        try
        {
            using var replacement = JsonDocument.Parse(rawValue);
            return replacement.RootElement.ValueKind == JsonValueKind.Object
                ? replacement.RootElement.EnumerateObject().Select(entry => entry.Name).ToArray()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? CheckAgentExistence(ConfigDocument document, string keyPath, IReadOnlyList<string> agentIds)
    {
        foreach (var agentId in agentIds)
        {
            // defaults is a reserved settings scope, not a lifecycle-managed named agent.
            if (!agentId.Equals("defaults", StringComparison.OrdinalIgnoreCase)
                && document.FindEntryKey("agents", agentId) is null)
            {
                return $"Configuration path '{keyPath}' targets unknown agent '{agentId}'. "
                    + "Run 'botnexus agents list' to list agents or 'botnexus agents add' to create one.";
            }
        }
        return null;
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
        if (!ConfigPresence.Exists(configPath))
        {
            AnsiConsole.MarkupLine(configPath.NotFoundMessage());
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

