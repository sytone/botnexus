using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// CLI subcommands for inspecting BotNexus log files directly without
/// requiring a running gateway instance. Reads hourly-rotated log files
/// from ~/.botnexus/logs/ with pattern botnexus-YYYYMMDDH.log.
/// </summary>
internal sealed class DebugLogsCommand
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Regex to parse Serilog structured log lines.
    /// Format: 2026-06-10 14:30:15.123 +00:00 [INF] Message text
    /// </summary>
    internal static readonly Regex LogLinePattern = new(
        @"^(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3})\s*([+-]\d{2}:\d{2})?\s*\[(\w{3})\]\s*(.*)",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    public Command Build(Option<string?> targetOption)
    {
        var command = new Command("logs", "Inspect log files directly (offline, no gateway required).");

        var formatOption = new Option<string>("--format", () => "table", "Output format: table or json.");
        command.AddOption(formatOption);

        // ── tail ──
        var limitOption = new Option<int>("--limit", () => 50, "Maximum lines to return.");
        var levelOption = new Option<string?>("--level", "Filter by log level: debug, info, warn, error.");

        var tailCommand = new Command("tail", "Show most recent log lines.")
        {
            limitOption, levelOption
        };
        tailCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var limit = context.ParseResult.GetValueForOption(limitOption);
            var level = context.ParseResult.GetValueForOption(levelOption);
            var logsDir = ResolveLogsDir(target);
            context.ExitCode = ExecuteTail(logsDir, limit, level, format);
            return Task.CompletedTask;
        });

        // ── errors ──
        var errorsLimitOption = new Option<int>("--limit", () => 20, "Maximum error lines to return.");
        var errorsCommand = new Command("errors", "Show recent error log lines (shorthand for tail --level error).")
        {
            errorsLimitOption
        };
        errorsCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var limit = context.ParseResult.GetValueForOption(errorsLimitOption);
            var logsDir = ResolveLogsDir(target);
            context.ExitCode = ExecuteTail(logsDir, limit, "error", format);
            return Task.CompletedTask;
        });

        // ── search ──
        var termOption = new Option<string>("--term", "Keyword to search for.") { IsRequired = true };
        var sinceOption = new Option<string?>("--since", "Only search log files after this datetime (ISO format).");
        var searchLimitOption = new Option<int>("--limit", () => 50, "Maximum matching lines to return.");

        var searchCommand = new Command("search", "Search across log files for a keyword.")
        {
            termOption, sinceOption, searchLimitOption
        };
        searchCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var term = context.ParseResult.GetValueForOption(termOption)!;
            var since = context.ParseResult.GetValueForOption(sinceOption);
            var limit = context.ParseResult.GetValueForOption(searchLimitOption);
            var logsDir = ResolveLogsDir(target);
            context.ExitCode = ExecuteSearch(logsDir, term, since, limit, format);
            return Task.CompletedTask;
        });

        // ── session ──
        var sessionIdArg = new Argument<string>("session-id", "Session ID to search for in logs.");
        var sessionLimitOption = new Option<int>("--limit", () => 100, "Maximum matching lines to return.");

        var sessionCommand = new Command("session", "Find all log lines mentioning a session ID.")
        {
            sessionIdArg, sessionLimitOption
        };
        sessionCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var sessionId = context.ParseResult.GetValueForArgument(sessionIdArg);
            var limit = context.ParseResult.GetValueForOption(sessionLimitOption);
            var logsDir = ResolveLogsDir(target);
            context.ExitCode = ExecuteSearch(logsDir, sessionId, null, limit, format);
            return Task.CompletedTask;
        });

        command.AddCommand(tailCommand);
        command.AddCommand(errorsCommand);
        command.AddCommand(searchCommand);
        command.AddCommand(sessionCommand);
        return command;
    }

    internal static string ResolveLogsDir(string? target)
    {
        var home = CliPaths.ResolveTarget(target);
        return Path.Combine(home, "logs");
    }

    internal static int ExecuteTail(string logsDir, int limit, string? level, string format)
    {
        if (!Directory.Exists(logsDir))
        {
            AnsiConsole.MarkupLine("[red]Logs directory not found:[/] " + Markup.Escape(logsDir));
            return 1;
        }

        var lines = TailLines(logsDir, limit, level);

        if (lines.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No log lines found matching criteria.[/]");
            return 0;
        }

        OutputLines(lines, format);
        return 0;
    }

    internal static int ExecuteSearch(string logsDir, string term, string? since, int limit, string format)
    {
        if (!Directory.Exists(logsDir))
        {
            AnsiConsole.MarkupLine("[red]Logs directory not found:[/] " + Markup.Escape(logsDir));
            return 1;
        }

        DateTime? sinceDate = null;
        if (since is not null)
        {
            if (!DateTime.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                AnsiConsole.MarkupLine($"[red]Invalid --since datetime:[/] {Markup.Escape(since)}");
                return 1;
            }
            sinceDate = parsed.ToLocalTime();
        }

        var lines = SearchLines(logsDir, term, sinceDate, limit);

        if (lines.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No log lines found containing:[/] {Markup.Escape(term)}");
            return 0;
        }

        OutputLines(lines, format);
        return 0;
    }

    // ── Core log reading logic ──

    internal static List<LogEntry> TailLines(string logsDir, int limit, string? level)
    {
        var files = GetLogFilesSorted(logsDir);
        if (files.Count == 0) return [];

        var result = new List<LogEntry>();
        var normalizedLevel = NormalizeLevel(level);

        // Read files from most recent backwards until we have enough lines
        for (var i = files.Count - 1; i >= 0 && result.Count < limit; i--)
        {
            var fileLines = File.ReadAllLines(files[i]);
            for (var j = fileLines.Length - 1; j >= 0 && result.Count < limit; j--)
            {
                var entry = ParseLogLine(fileLines[j], files[i]);
                if (entry is null) continue;
                if (normalizedLevel is not null && !string.Equals(entry.Level, normalizedLevel, StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(entry);
            }
        }

        // Reverse to chronological order
        result.Reverse();
        return result;
    }

    internal static List<LogEntry> SearchLines(string logsDir, string term, DateTime? sinceLocal, int limit)
    {
        var files = GetLogFilesSorted(logsDir);
        if (files.Count == 0) return [];

        var result = new List<LogEntry>();

        foreach (var file in files)
        {
            // Skip files before the 'since' date based on filename
            if (sinceLocal is not null)
            {
                var fileTime = ParseFileTimestamp(file);
                if (fileTime is not null && fileTime.Value.AddHours(1) <= sinceLocal.Value)
                    continue;
            }

            foreach (var line in File.ReadLines(file))
            {
                if (result.Count >= limit) break;
                if (line.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    var entry = ParseLogLine(line, file) ?? new LogEntry { Message = line, SourceFile = Path.GetFileName(file) };
                    result.Add(entry);
                }
            }

            if (result.Count >= limit) break;
        }

        return result;
    }

    internal static List<string> GetLogFilesSorted(string logsDir)
    {
        if (!Directory.Exists(logsDir)) return [];

        return Directory.GetFiles(logsDir, "botnexus-*.log")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    internal static LogEntry? ParseLogLine(string line, string? sourceFile = null)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var match = LogLinePattern.Match(line);
        if (!match.Success)
        {
            // Non-structured line (continuation, stack trace, etc.)
            return new LogEntry
            {
                Message = line,
                SourceFile = sourceFile is not null ? Path.GetFileName(sourceFile) : null
            };
        }

        return new LogEntry
        {
            Timestamp = match.Groups[1].Value,
            Offset = match.Groups[2].Value,
            Level = NormalizeLevelAbbrev(match.Groups[3].Value),
            Message = match.Groups[4].Value,
            SourceFile = sourceFile is not null ? Path.GetFileName(sourceFile) : null
        };
    }

    internal static DateTime? ParseFileTimestamp(string filePath)
    {
        // Pattern: botnexus-YYYYMMDDH.log or botnexus-YYYYMMDDHH.log
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        if (!fileName.StartsWith("botnexus-", StringComparison.Ordinal)) return null;

        var datePart = fileName["botnexus-".Length..];
        // Try YYYYMMDDHH (10 chars) then YYYYMMDDH (9 chars)
        if (datePart.Length >= 10 &&
            DateTime.TryParseExact(datePart[..10], "yyyyMMddHH", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt10))
            return dt10;
        if (datePart.Length >= 9 &&
            int.TryParse(datePart[..8], out _) &&
            int.TryParse(datePart[8..9], out var hour9) && hour9 is >= 0 and <= 9)
        {
            if (DateTime.TryParseExact(datePart[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt8))
                return dt8.AddHours(hour9);
        }

        return null;
    }

    private static string? NormalizeLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "debug" or "dbg" or "verbose" or "vrb" => "DBG",
        "info" or "information" or "inf" => "INF",
        "warn" or "warning" or "wrn" => "WRN",
        "error" or "err" or "fatal" or "ftl" => "ERR",
        null => null,
        _ => level.ToUpperInvariant()
    };

    private static string NormalizeLevelAbbrev(string abbrev) => abbrev.ToUpperInvariant() switch
    {
        "DBG" or "VRB" => "DBG",
        "INF" => "INF",
        "WRN" => "WRN",
        "ERR" or "FTL" => "ERR",
        _ => abbrev.ToUpperInvariant()
    };

    private static void OutputLines(List<LogEntry> lines, string format)
    {
        if (format == "json")
        {
            AnsiConsole.Write(new Text(JsonSerializer.Serialize(lines, JsonOpts)));
            AnsiConsole.WriteLine();
            return;
        }

        foreach (var entry in lines)
        {
            var levelMarkup = entry.Level switch
            {
                "ERR" => "[red]ERR[/]",
                "WRN" => "[yellow]WRN[/]",
                "INF" => "[green]INF[/]",
                "DBG" => "[dim]DBG[/]",
                _ => Markup.Escape(entry.Level ?? "???")
            };

            if (entry.Timestamp is not null)
            {
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(entry.Timestamp)}[/] {levelMarkup} {Markup.Escape(entry.Message ?? "")}");
            }
            else
            {
                AnsiConsole.MarkupLine($"  {Markup.Escape(entry.Message ?? "")}");
            }
        }

        AnsiConsole.MarkupLine($"\n[dim]{lines.Count} line(s) shown.[/]");
    }

    // ── DTO ──

    internal sealed class LogEntry
    {
        public string? Timestamp { get; set; }
        public string? Offset { get; set; }
        public string? Level { get; set; }
        public string? Message { get; set; }
        public string? SourceFile { get; set; }
    }
}
