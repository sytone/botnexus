using System.CommandLine;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// CLI subcommands for raw database introspection of BotNexus SQLite stores.
/// Provides tables, schema, and size operations for offline diagnostics
/// without requiring a running gateway instance.
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

        var dbOption = new Option<string?>("--db", "Target database: sessions, cron, or memory.");

        // ── tables ──
        var tablesCommand = new Command("tables", "List tables with row counts.") { dbOption };
        tablesCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var db = context.ParseResult.GetValueForOption(dbOption);
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = ExecuteTables(home, db, format);
            return Task.CompletedTask;
        });

        // ── schema ──
        var schemaCommand = new Command("schema", "Show CREATE TABLE statements.") { dbOption };
        schemaCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var db = context.ParseResult.GetValueForOption(dbOption);
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = ExecuteSchema(home, db, format);
            return Task.CompletedTask;
        });

        // ── size ──
        var sizeCommand = new Command("size", "Show file sizes for all .db files.");
        sizeCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = ExecuteSize(home, format);
            return Task.CompletedTask;
        });

        command.AddCommand(tablesCommand);
        command.AddCommand(schemaCommand);
        command.AddCommand(sizeCommand);
        return command;
    }

    internal static int ExecuteTables(string home, string? db, string format)
    {
        var dbFiles = ResolveDbFiles(home, db);
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
                table.AddRow(entry.Database, entry.Table, entry.RowCount.ToString("N0"));

            AnsiConsole.Write(table);
        }

        return 0;
    }

    internal static int ExecuteSchema(string home, string? db, string format)
    {
        var dbFiles = ResolveDbFiles(home, db);
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

    internal static int ExecuteSize(string home, string format)
    {
        if (!Directory.Exists(home))
        {
            AnsiConsole.MarkupLine("[red]BotNexus home directory not found:[/] " + Markup.Escape(home));
            return 1;
        }

        var dbFiles = Directory.GetFiles(home, "*.db", SearchOption.TopDirectoryOnly)
            .Select(p => new FileInfo(p))
            .OrderBy(f => f.Name)
            .ToList();

        // Also check for WAL and journal files
        var walFiles = Directory.GetFiles(home, "*.db-wal", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(home, "*.db-shm", SearchOption.TopDirectoryOnly))
            .Select(p => new FileInfo(p))
            .OrderBy(f => f.Name)
            .ToList();

        if (dbFiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No .db files found in:[/] " + Markup.Escape(home));
            return 0;
        }

        var entries = dbFiles.Select(f => new SizeEntry
        {
            File = f.Name,
            SizeBytes = f.Length,
            SizeFormatted = FormatSize(f.Length)
        }).ToList();

        foreach (var wal in walFiles)
        {
            entries.Add(new SizeEntry
            {
                File = wal.Name,
                SizeBytes = wal.Length,
                SizeFormatted = FormatSize(wal.Length)
            });
        }

        entries = entries.OrderBy(e => e.File).ToList();

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
                table.AddRow(entry.File, entry.SizeFormatted);

            var totalBytes = entries.Sum(e => e.SizeBytes);
            table.AddRow("[bold]Total[/]", $"[bold]{FormatSize(totalBytes)}[/]");
            AnsiConsole.Write(table);
        }

        return 0;
    }

    internal static DbFileInfo[] ResolveDbFiles(string home, string? db)
    {
        if (!string.IsNullOrEmpty(db))
        {
            var fileName = db.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ? db : $"{db}.db";
            var path = Path.Combine(home, fileName);
            return [new DbFileInfo(db, path)];
        }

        if (!Directory.Exists(home))
            return [];

        return Directory.GetFiles(home, "*.db", SearchOption.TopDirectoryOnly)
            .Select(p => new DbFileInfo(Path.GetFileNameWithoutExtension(p), p))
            .OrderBy(f => f.Name)
            .ToArray();
    }

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
