using System.CommandLine;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// CLI subcommands for raw database introspection of BotNexus SQLite stores.
/// Provides tables, schema, and size operations for offline diagnostics
/// without requiring a running gateway instance.
/// <para>
/// Database discovery is the whole point of this command: it enumerates every
/// registered platform store, not just files ending in <c>.db</c>. BotNexus mixes
/// two SQLite file extensions — <c>.db</c> (sessions, skill-usage) and
/// <c>.sqlite</c> (cron, webhooks, agent memory) — and stores some databases in a
/// <c>data/</c> subfolder rather than the home root. Historically this command only
/// globbed top-level <c>*.db</c>, so <c>cron.sqlite</c>, <c>webhooks.sqlite</c>, and
/// <c>data/skill-usage.db</c> were silently invisible. That is why manual
/// <c>sqlite3</c> scripting was needed for investigations; discovery now covers all
/// of them so this command is the first-line tool.
/// </para>
/// </summary>
internal sealed class DebugDbCommand
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Command Build(Option<string?> targetOption)
    {
        var command = new Command("db", "Raw database introspection (offline, no gateway required).");

        var formatOption = new Option<string>("--format", () => "table", "Output format: table or json.");
        command.AddOption(formatOption);

        var dbOption = new Option<string?>("--db", "Filter to a single database by name (e.g. sessions, cron, webhooks, skill-usage).");
        var includeAgentsOption = new Option<bool>("--include-agents", "Also include per-agent memory databases (agents/<id>/data/memory.sqlite).");

        // ── tables ──
        var tablesCommand = new Command("tables", "List tables with row counts across all registered databases.") { dbOption, includeAgentsOption };
        tablesCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var db = context.ParseResult.GetValueForOption(dbOption);
            var includeAgents = context.ParseResult.GetValueForOption(includeAgentsOption);
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = ExecuteTables(home, db, format, includeAgents);
            return Task.CompletedTask;
        });

        // ── schema ──
        var schemaCommand = new Command("schema", "Show CREATE TABLE statements across all registered databases.") { dbOption, includeAgentsOption };
        schemaCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var db = context.ParseResult.GetValueForOption(dbOption);
            var includeAgents = context.ParseResult.GetValueForOption(includeAgentsOption);
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = ExecuteSchema(home, db, format, includeAgents);
            return Task.CompletedTask;
        });

        // ── size ──
        var sizeCommand = new Command("size", "Show file sizes for all registered database files.") { includeAgentsOption };
        sizeCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var includeAgents = context.ParseResult.GetValueForOption(includeAgentsOption);
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = ExecuteSize(home, format, includeAgents);
            return Task.CompletedTask;
        });

        command.AddCommand(tablesCommand);
        command.AddCommand(schemaCommand);
        command.AddCommand(sizeCommand);
        return command;
    }

    internal static int ExecuteTables(string home, string? db, string format, bool includeAgents = false)
    {
        var dbFiles = ResolveDbFiles(home, db, includeAgents);
        if (dbFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]No database files found.[/]");
            return 1;
        }

        var allTables = new List<TableEntry>();

        foreach (var dbFile in dbFiles)
        {
            if (!File.Exists(dbFile.Path))
            {
                AnsiConsole.MarkupLine($"[yellow]Skipping {Markup.Escape(dbFile.Name)}: file not found.[/]");
                continue;
            }

            try
            {
                using var connection = OpenReadOnly(dbFile.Path);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var tableName = reader.GetString(0);
                    var rowCount = GetRowCount(connection, tableName);
                    allTables.Add(new TableEntry { Database = dbFile.Name, Table = tableName, RowCount = rowCount });
                }
            }
            catch (SqliteException ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Skipping {Markup.Escape(dbFile.Name)}: {Markup.Escape(ex.Message)}[/]");
            }
        }

        if (format == "json")
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(allTables, JsonOpts));
        }
        else
        {
            var table = new Table();
            table.AddColumn("Database");
            table.AddColumn("Table");
            table.AddColumn(new TableColumn("Rows").RightAligned());

            foreach (var entry in allTables)
                table.AddRow(Markup.Escape(entry.Database), Markup.Escape(entry.Table), entry.RowCount.ToString("N0"));

            AnsiConsole.Write(table);
        }

        return 0;
    }

    internal static int ExecuteSchema(string home, string? db, string format, bool includeAgents = false)
    {
        var dbFiles = ResolveDbFiles(home, db, includeAgents);
        if (dbFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]No database files found.[/]");
            return 1;
        }

        var schemas = new List<SchemaEntry>();

        foreach (var dbFile in dbFiles)
        {
            if (!File.Exists(dbFile.Path))
            {
                AnsiConsole.MarkupLine($"[yellow]Skipping {Markup.Escape(dbFile.Name)}: file not found.[/]");
                continue;
            }

            try
            {
                using var connection = OpenReadOnly(dbFile.Path);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    schemas.Add(new SchemaEntry
                    {
                        Database = dbFile.Name,
                        Table = reader.GetString(0),
                        Ddl = reader.IsDBNull(1) ? string.Empty : reader.GetString(1)
                    });
                }
            }
            catch (SqliteException ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Skipping {Markup.Escape(dbFile.Name)}: {Markup.Escape(ex.Message)}[/]");
            }
        }

        if (format == "json")
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(schemas, JsonOpts));
        }
        else
        {
            foreach (var entry in schemas)
            {
                AnsiConsole.MarkupLine($"[bold]{Markup.Escape(entry.Database)}[/] → [cyan]{Markup.Escape(entry.Table)}[/]");
                AnsiConsole.WriteLine(entry.Ddl);
                AnsiConsole.WriteLine();
            }
        }

        return 0;
    }

    internal static int ExecuteSize(string home, string format, bool includeAgents = false)
    {
        if (!Directory.Exists(home))
        {
            AnsiConsole.MarkupLine("[red]BotNexus home directory not found:[/] " + Markup.Escape(home));
            return 1;
        }

        var dbFiles = ResolveDbFiles(home, null, includeAgents);

        if (dbFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No database files found in:[/] " + Markup.Escape(home));
            return 0;
        }

        var entries = new List<SizeEntry>();

        foreach (var dbFile in dbFiles)
        {
            if (!File.Exists(dbFile.Path))
                continue;

            var fi = new FileInfo(dbFile.Path);
            entries.Add(new SizeEntry
            {
                File = dbFile.Name,
                SizeBytes = fi.Length,
                SizeFormatted = FormatSize(fi.Length)
            });

            // Include sidecar WAL and shared-memory files so the reported size
            // reflects on-disk footprint of an actively-written store.
            foreach (var (suffix, label) in new[] { ("-wal", " (wal)"), ("-shm", " (shm)") })
            {
                var sidecar = dbFile.Path + suffix;
                if (!File.Exists(sidecar))
                    continue;

                var sfi = new FileInfo(sidecar);
                entries.Add(new SizeEntry
                {
                    File = dbFile.Name + label,
                    SizeBytes = sfi.Length,
                    SizeFormatted = FormatSize(sfi.Length)
                });
            }
        }

        entries = entries.OrderBy(e => e.File, StringComparer.OrdinalIgnoreCase).ToList();

        if (format == "json")
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(entries, JsonOpts));
        }
        else
        {
            var table = new Table();
            table.AddColumn("File");
            table.AddColumn(new TableColumn("Size").RightAligned());

            foreach (var entry in entries)
                table.AddRow(Markup.Escape(entry.File), entry.SizeFormatted);

            var totalBytes = entries.Sum(e => e.SizeBytes);
            table.AddRow("[bold]Total[/]", $"[bold]{FormatSize(totalBytes)}[/]");
            AnsiConsole.Write(table);
        }

        return 0;
    }

    /// <summary>
    /// Discover every registered BotNexus database under <paramref name="home"/>.
    /// Covers both SQLite file extensions (<c>.db</c> and <c>.sqlite</c>), the home
    /// root and the <c>data/</c> subfolder, and — when <paramref name="includeAgents"/>
    /// is set — per-agent memory stores at <c>agents/&lt;id&gt;/data/memory.sqlite</c>.
    /// </summary>
    /// <param name="home">BotNexus home directory (e.g. ~/.botnexus).</param>
    /// <param name="db">Optional single-database filter matched against the discovered display name.</param>
    /// <param name="includeAgents">When true, also enumerate per-agent memory databases.</param>
    internal static DbFileInfo[] ResolveDbFiles(string home, string? db, bool includeAgents = false)
    {
        if (!Directory.Exists(home))
            return [];

        var discovered = DiscoverAll(home, includeAgents);

        if (string.IsNullOrEmpty(db))
            return discovered;

        // Normalize the requested name: allow "cron", "cron.sqlite", "cron.db",
        // or a discovered display label ("data/skill-usage").
        var requested = db.Trim();
        var requestedNoExt = requested;
        if (requestedNoExt.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            requestedNoExt = requestedNoExt[..^3];
        else if (requestedNoExt.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
            requestedNoExt = requestedNoExt[..^7];

        var matches = discovered.Where(f =>
                string.Equals(f.Name, requested, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.Name, requestedNoExt, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(f.Path), requested, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileNameWithoutExtension(f.Path), requestedNoExt, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // If no discovered file matches, fall back to a direct path probe so that
        // `--db memory` (a store that may not exist yet) still returns a sensible
        // skip warning rather than "no databases found".
        if (matches.Length == 0)
        {
            var fileName = requested.Contains('.', StringComparison.Ordinal) ? requested : $"{requested}.db";
            return [new DbFileInfo(requestedNoExt, Path.Combine(home, fileName))];
        }

        return matches;
    }

    private static DbFileInfo[] DiscoverAll(string home, bool includeAgents)
    {
        var results = new List<DbFileInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Home root: both extensions.
        foreach (var path in EnumerateSqliteFiles(home, SearchOption.TopDirectoryOnly))
        {
            if (seen.Add(path))
                results.Add(new DbFileInfo(Path.GetFileNameWithoutExtension(path), path));
        }

        // data/ subfolder (e.g. data/skill-usage.db).
        var dataDir = Path.Combine(home, "data");
        if (Directory.Exists(dataDir))
        {
            foreach (var path in EnumerateSqliteFiles(dataDir, SearchOption.TopDirectoryOnly))
            {
                if (seen.Add(path))
                    results.Add(new DbFileInfo("data/" + Path.GetFileNameWithoutExtension(path), path));
            }
        }

        // Per-agent memory stores (opt-in — there can be hundreds).
        if (includeAgents)
        {
            var agentsDir = Path.Combine(home, "agents");
            if (Directory.Exists(agentsDir))
            {
                foreach (var agentDir in Directory.GetDirectories(agentsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var agentDataDir = Path.Combine(agentDir, "data");
                    if (!Directory.Exists(agentDataDir))
                        continue;

                    foreach (var path in EnumerateSqliteFiles(agentDataDir, SearchOption.TopDirectoryOnly))
                    {
                        if (seen.Add(path))
                        {
                            var label = $"{Path.GetFileName(agentDir)}/{Path.GetFileNameWithoutExtension(path)}";
                            results.Add(new DbFileInfo(label, path));
                        }
                    }
                }
            }
        }

        return results
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateSqliteFiles(string directory, SearchOption option) =>
        Directory.GetFiles(directory, "*.db", option)
            .Concat(Directory.GetFiles(directory, "*.sqlite", option));

    private static long GetRowCount(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM [{tableName}];";
        return (long)(cmd.ExecuteScalar() ?? 0);
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };

    internal sealed record DbFileInfo(string Name, string Path);

    private sealed class TableEntry
    {
        public string Database { get; init; } = string.Empty;
        public string Table { get; init; } = string.Empty;
        public long RowCount { get; init; }
    }

    private sealed class SchemaEntry
    {
        public string Database { get; init; } = string.Empty;
        public string Table { get; init; } = string.Empty;
        public string Ddl { get; init; } = string.Empty;
    }

    private sealed class SizeEntry
    {
        public string File { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public string SizeFormatted { get; init; } = string.Empty;
    }
}
