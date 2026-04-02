using System.CommandLine;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BotNexus.Cli.Services;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Diagnostics;
using BotNexus.Diagnostics.Checkups.Configuration;
using Spectre.Console;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var homeOption = new Option<string?>("--home")
{
    Description = "Override BOTNEXUS_HOME for this command."
};
homeOption.Recursive = true;

var configManager = new ConfigFileManager();
var cliVersion = ResolveCliVersion();
if (args.Length == 1 && string.Equals(args[0], "--version", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"botnexus {cliVersion}");
    return 0;
}

ConsoleOutput.WriteBanner(cliVersion);

var rootCommand = new RootCommand("BotNexus CLI — manage agents, providers, and the Gateway.");
rootCommand.Add(homeOption);

rootCommand.Add(BuildConfigCommand(homeOption, configManager));
rootCommand.Add(BuildAgentCommand(homeOption, configManager));
rootCommand.Add(BuildProviderCommand(homeOption, configManager));
rootCommand.Add(BuildChannelCommand(homeOption, configManager));
rootCommand.Add(BuildExtensionCommand(homeOption, configManager));
rootCommand.Add(BuildDoctorCommand(homeOption));
rootCommand.Add(BuildStatusCommand(homeOption, configManager));
rootCommand.Add(BuildLogsCommand(homeOption));
rootCommand.Add(BuildBackupCommand(homeOption));
rootCommand.Add(BuildInstallCommand(homeOption));
rootCommand.Add(BuildUpdateCommand(homeOption, configManager));
rootCommand.Add(BuildStartCommand(homeOption, configManager));
rootCommand.Add(BuildStopCommand(homeOption));
rootCommand.Add(BuildRestartCommand(homeOption, configManager));

return rootCommand.Parse(args).Invoke();

static Command BuildConfigCommand(Option<string?> homeOption, ConfigFileManager configManager)
{
    var configCommand = new Command("config", "Manage BotNexus config.");

    var validateCommand = new Command("validate", "Validate config.json syntax and binding.");
    validateCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        if (configManager.TryValidateConfig(homePath, out _, out var message))
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Success, message);
            return 0;
        }

        ConsoleOutput.WriteStatus(ConsoleStatus.Error, message);
        return 1;
    });

    var showCommand = new Command("show", "Show resolved config (defaults merged with overrides).");
    showCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        var config = configManager.LoadConfig(homePath);
        ConsoleOutput.WriteJson(new Dictionary<string, BotNexusConfig>
        {
            [BotNexusConfig.SectionName] = config
        });
        return 0;
    });

    var initCommand = new Command("init", "Create default config.json interactively.");
    initCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        var configPath = configManager.GetConfigPath(homePath);
        if (File.Exists(configPath))
        {
            if (!ConsoleOutput.Confirm("config.json already exists. Overwrite?"))
            {
                ConsoleOutput.WriteStatus(ConsoleStatus.Warning, "Canceled.");
                return 1;
            }
        }

        var config = new BotNexusConfig();
        var providerName = ConsoleOutput.Prompt("Provider", "copilot");
        var model = ConsoleOutput.Prompt("Model", config.Agents.Model);
        var portText = ConsoleOutput.Prompt("Gateway port", config.Gateway.Port.ToString());
        if (!int.TryParse(portText, out var port))
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Error, $"Invalid port: {portText}");
            return 1;
        }

        config.Agents.Model = model;
        config.Gateway.Port = port;
        config.Providers[providerName] = new ProviderConfig
        {
            Auth = providerName.Equals("copilot", StringComparison.OrdinalIgnoreCase) ? "oauth" : "apikey",
            DefaultModel = model
        };

        configManager.SaveConfig(homePath, config);
        ConsoleOutput.WriteStatus(ConsoleStatus.Success, $"Initialized config at {configPath}");
        return 0;
    });

    configCommand.Add(validateCommand);
    configCommand.Add(showCommand);
    configCommand.Add(initCommand);
    return configCommand;
}

static Command BuildAgentCommand(Option<string?> homeOption, ConfigFileManager configManager)
{
    var agentCommand = new Command("agent", "Manage agents.");

    var addCommand = new Command("add", "Add an agent to config.");
    addCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        var config = configManager.LoadConfig(homePath);
        var name = ConsoleOutput.Prompt("Agent name", string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Error, "Agent name is required.");
            return 1;
        }

        var provider = ConsoleOutput.Prompt("Provider", config.Providers.Keys.FirstOrDefault() ?? "copilot");
        var model = ConsoleOutput.Prompt("Model", config.Agents.Model);
        configManager.AddAgent(homePath, name, new AgentConfig
        {
            Name = name,
            Provider = provider,
            Model = model
        });

        ConsoleOutput.WriteStatus(ConsoleStatus.Success, $"Added agent '{name}'.");
        return 0;
    });

    var listCommand = new Command("list", "List configured agents.");
    listCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        var agents = configManager.LoadConfig(homePath).Agents.Named;
        if (agents.Count == 0)
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Warning, "No named agents configured.");
            return 0;
        }

        ConsoleOutput.WriteTable(
            ["name", "provider", "model", "memory enabled"],
            agents.OrderBy(a => a.Key).Select(a => new[]
            {
                a.Key,
                a.Value.Provider ?? string.Empty,
                a.Value.Model ?? string.Empty,
                a.Value.EnableMemory == true ? "yes" : "no"
            }));
        return 0;
    });

    var nameArgument = new Argument<string>("name")
    {
        Description = "Agent name"
    };
    var workspaceCommand = new Command("workspace", "Show agent workspace path and files.")
    {
        nameArgument
    };
    workspaceCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        var name = parseResult.GetValue(nameArgument);
        if (string.IsNullOrWhiteSpace(name))
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Error, "Agent name is required.");
            return 1;
        }

        var workspacePath = Path.Combine(homePath, "agents", name);
        ConsoleOutput.WriteHeader("Workspace");
        Console.WriteLine(workspacePath);

        if (!Directory.Exists(workspacePath))
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Warning, "Workspace not found.");
            return 0;
        }

        var entries = Directory.EnumerateFileSystemEntries(workspacePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFileName)
            .Select(fileName => new[] { fileName ?? string.Empty });
        ConsoleOutput.WriteTable(["files"], entries);
        return 0;
    });

    agentCommand.Add(addCommand);
    agentCommand.Add(listCommand);
    agentCommand.Add(workspaceCommand);
    return agentCommand;
}

static Command BuildProviderCommand(Option<string?> homeOption, ConfigFileManager configManager)
{
    var providerCommand = new Command("provider", "Manage providers.");

    var addCommand = new Command("add", "Add a provider to config.");
    addCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        var name = ConsoleOutput.Prompt("Provider name", string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Error, "Provider name is required.");
            return 1;
        }

        var auth = ConsoleOutput.Prompt("Auth type", "apikey");
        var apiBase = ConsoleOutput.Prompt("API base", string.Empty);
        var defaultModel = ConsoleOutput.Prompt("Default model", string.Empty);

        configManager.AddProvider(homePath, name, new ProviderConfig
        {
            Auth = auth,
            ApiBase = string.IsNullOrWhiteSpace(apiBase) ? null : apiBase,
            DefaultModel = string.IsNullOrWhiteSpace(defaultModel) ? null : defaultModel
        });

        ConsoleOutput.WriteStatus(ConsoleStatus.Success, $"Added provider '{name}'.");
        return 0;
    });

    var listCommand = new Command("list", "List configured providers.");
    listCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        var providers = configManager.LoadConfig(homePath).Providers;
        if (providers.Count == 0)
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Warning, "No providers configured.");
            return 0;
        }

        ConsoleOutput.WriteTable(
            ["name", "auth type", "default model"],
            providers.OrderBy(p => p.Key).Select(p => new[]
            {
                p.Key,
                p.Value.Auth,
                p.Value.DefaultModel ?? string.Empty
            }));
        return 0;
    });

    providerCommand.Add(addCommand);
    providerCommand.Add(listCommand);
    return providerCommand;
}

static Command BuildChannelCommand(Option<string?> homeOption, ConfigFileManager configManager)
{
    var channelCommand = new Command("channel", "Manage channels.");

    var addCommand = new Command("add", "Add a channel instance to config.");
    addCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        var type = ConsoleOutput.Select("Channel type", new[] { "discord", "slack", "telegram" });
        if (!new[] { "discord", "slack", "telegram" }.Contains(type, StringComparer.OrdinalIgnoreCase))
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Error, "Channel type must be discord, slack, or telegram.");
            return 1;
        }

        var token = ConsoleOutput.Prompt("Bot token", string.Empty);
        if (string.IsNullOrWhiteSpace(token))
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Error, "Token is required.");
            return 1;
        }

        configManager.AddChannel(homePath, type, new ChannelConfig
        {
            Enabled = true,
            BotToken = token
        });
        ConsoleOutput.WriteStatus(ConsoleStatus.Success, $"Added channel '{type}'.");
        return 0;
    });

    channelCommand.Add(addCommand);
    return channelCommand;
}

static Command BuildExtensionCommand(Option<string?> homeOption, ConfigFileManager configManager)
{
    var extensionCommand = new Command("extension", "Manage extensions.");
    var listCommand = new Command("list", "List installed extensions from extensions folder.");
    listCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        var config = configManager.LoadConfig(homePath);
        var extensionsRoot = Path.Combine(Directory.GetCurrentDirectory(), "extensions");
        if (!Directory.Exists(extensionsRoot))
            extensionsRoot = BotNexusHome.ResolvePath(config.ExtensionsPath);

        if (!Directory.Exists(extensionsRoot))
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Warning, $"Extensions folder not found: {extensionsRoot}");
            return 1;
        }

        var rows = new List<string[]>();
        foreach (var typeDirectory in Directory.EnumerateDirectories(extensionsRoot).OrderBy(x => x))
        {
            var type = Path.GetFileName(typeDirectory);
            foreach (var extensionDirectory in Directory.EnumerateDirectories(typeDirectory).OrderBy(x => x))
            {
                var files = Directory.EnumerateFiles(extensionDirectory, "*", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();
                rows.Add([
                    type,
                    Path.GetFileName(extensionDirectory),
                    extensionDirectory,
                    files.Count == 0 ? "-" : string.Join(", ", files.Take(4))
                ]);
            }
        }

        if (rows.Count == 0)
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Warning, "No installed extensions found.");
            return 0;
        }

        ConsoleOutput.WriteTable(["type", "name", "path", "files"], rows);
        return 0;
    });

    extensionCommand.Add(listCommand);
    return extensionCommand;
}

static Command BuildDoctorCommand(Option<string?> homeOption)
{
    var categoryOption = new Option<string?>("--category")
    {
        Description = "Filter checkups by category."
    };

    var doctorCommand = new Command("doctor", "Run health checkups.");
    doctorCommand.Add(categoryOption);
    doctorCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        var category = parseResult.GetValue(categoryOption);
        var diagnosticsPaths = new DiagnosticsPaths(homePath, Path.Combine(homePath, "config.json"));
        var checkups = new List<IHealthCheckup> { new ConfigValidCheckup(diagnosticsPaths) };
        var selectedCheckups = checkups
            .Where(c => string.IsNullOrWhiteSpace(category) ||
                        string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var runner = new CheckupRunner(checkups);
        var results = runner.RunAllAsync(category).GetAwaiter().GetResult();

        if (results.Count == 0)
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Warning, "No checkups matched the category filter.");
            return 1;
        }

        ConsoleOutput.WriteHeader("BotNexus Doctor");
        var passCount = 0;
        var warnCount = 0;
        var failCount = 0;
        
        var doctorTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey);
        doctorTable.AddColumn(new TableColumn("[bold]Status[/]"));
        doctorTable.AddColumn(new TableColumn("[bold]Category[/]"));
        doctorTable.AddColumn(new TableColumn("[bold]Name[/]"));
        doctorTable.AddColumn(new TableColumn("[bold]Message[/]"));

        for (var index = 0; index < selectedCheckups.Count && index < results.Count; index++)
        {
            var checkup = selectedCheckups[index];
            var result = results[index];
            var statusMarkup = result.Status switch
            {
                CheckupStatus.Pass => "[green]PASS[/]",
                CheckupStatus.Warn => "[yellow]WARN[/]",
                _ => "[red]FAIL[/]"
            };

            switch (result.Status)
            {
                case CheckupStatus.Pass: passCount++; break;
                case CheckupStatus.Warn: warnCount++; break;
                default: failCount++; break;
            }

            var message = Markup.Escape(result.Message);
            if (!string.IsNullOrWhiteSpace(result.Advice))
                message += $"\n[dim]→ {Markup.Escape(result.Advice)}[/]";

            doctorTable.AddRow(statusMarkup, Markup.Escape(checkup.Category), Markup.Escape(checkup.Name), message);
        }

        AnsiConsole.Write(doctorTable);
        AnsiConsole.MarkupLine($"\n[bold]Summary:[/] [green]{passCount} passed[/], [yellow]{warnCount} warnings[/], [red]{failCount} failures[/]");
        return failCount > 0 ? 1 : 0;
    });

    return doctorCommand;
}

static Command BuildStatusCommand(Option<string?> homeOption, ConfigFileManager configManager)
{
    var statusCommand = new Command("status", "Show Gateway and configuration status.");
    statusCommand.SetAction(parseResult =>
    {
        var cliVersion = ResolveCliVersion();
        var homePath = ResolveHome(parseResult, homeOption);
        var config = configManager.LoadConfig(homePath);
        var gatewayUrl = BuildGatewayUrl(config.Gateway);
        var installPath = ResolveInstallPath(null);
        var installedVersion = ReadInstalledVersionInfo(installPath);
        var installedVersionValue = installedVersion.GetValueOrDefault();
        var installedVersionText = !installedVersion.HasValue
            ? "unknown (not installed)"
            : string.IsNullOrWhiteSpace(installedVersionValue.InstalledAtUtc)
                ? installedVersionValue.Version
                : $"{installedVersionValue.Version} (installed {installedVersionValue.InstalledAtUtc})";

        var pidPath = Path.Combine(homePath, "gateway.pid");
        var pid = File.Exists(pidPath) ? ReadPid(pidPath) : null;
        var hasRunningPid = pid is not null && IsRunning(pid.Value);

        var isOnline = false;
        using var client = new GatewayClient(gatewayUrl);
        try
        {
            isOnline = client.IsRunningAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or HttpRequestException)
        {
            isOnline = false;
        }

        var gatewayStatus = hasRunningPid
            ? $"Running (PID {pid!.Value}) on {gatewayUrl}"
            : isOnline
                ? $"Running (PID unknown) on {gatewayUrl}"
                : $"Offline on {gatewayUrl}";
        var versionMatch = !installedVersion.HasValue
            ? "⚠️ Installed version unavailable"
            : string.Equals(cliVersion, installedVersionValue.Version, StringComparison.OrdinalIgnoreCase)
                ? "✅"
                : "⚠️ CLI and installed versions differ";

        var sourceDisplay = !installedVersion.HasValue
            ? ""
            : installedVersionValue.Source switch
            {
                "dev" => $"dev ({installedVersionValue.PackagesPath})",
                "release" => "release (GitHub)",
                _ => installedVersionValue.Source ?? "unknown"
            };

        var statusTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .HideHeaders();
        statusTable.AddColumn("Key");
        statusTable.AddColumn("Value");
        statusTable.AddRow("[bold]CLI version[/]", Markup.Escape(cliVersion));
        statusTable.AddRow("[bold]Installed version[/]", Markup.Escape(installedVersionText));
        if (!string.IsNullOrEmpty(sourceDisplay))
        {
            statusTable.AddRow("[bold]Source[/]", Markup.Escape(sourceDisplay));
        }
        var gatewayColor = hasRunningPid || isOnline ? "green" : "red";
        statusTable.AddRow("[bold]Gateway[/]", $"[{gatewayColor}]{Markup.Escape(gatewayStatus)}[/]");
        statusTable.AddRow("[bold]Version match[/]", Markup.Escape(versionMatch));

        AnsiConsole.Write(new Panel(statusTable)
            .Header("[bold]BotNexus Status[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.CornflowerBlue));
        return 0;
    });

    return statusCommand;
}

static Command BuildLogsCommand(Option<string?> homeOption)
{
    var followOption = new Option<bool>("--follow", "-f")
    {
        Description = "Stream new lines as logs are written."
    };
    var linesOption = new Option<int>("--lines")
    {
        Description = "Number of lines to show.",
        DefaultValueFactory = _ => 50
    };

    var logsCommand = new Command("logs", "Tail Gateway logs.");
    logsCommand.Add(followOption);
    logsCommand.Add(linesOption);
    logsCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        var logsPath = Path.Combine(homePath, "logs");
        if (!Directory.Exists(logsPath))
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Error, $"Log directory not found: {logsPath}");
            return 1;
        }

        var latestLog = Directory.EnumerateFiles(logsPath, "*.log*", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
        if (latestLog is null)
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Warning, "No log files found.");
            return 1;
        }

        var lines = Math.Max(1, parseResult.GetValue(linesOption));
        var follow = parseResult.GetValue(followOption);

        var tailLines = File.ReadAllLines(latestLog.FullName).TakeLast(lines);
        foreach (var line in tailLines)
            Console.WriteLine(line);

        if (!follow)
            return 0;

        using var stream = new FileStream(
            latestLog.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        stream.Seek(0, SeekOrigin.End);
        var shouldStop = false;
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            shouldStop = true;
        };

        while (!shouldStop)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                Thread.Sleep(500);
                continue;
            }

            Console.WriteLine(line);
        }

        return 0;
    });

    return logsCommand;
}

static Command BuildBackupCommand(Option<string?> homeOption)
{
    var backupCommand = new Command("backup", "Backup and restore the BotNexus home directory.");

    var outputOption = new Option<string?>("--output")
    {
        Description = "Output path for the backup zip file."
    };
    var forceOption = new Option<bool>("--force")
    {
        Description = "Restore without confirmation prompt."
    };
    var backupPathArgument = new Argument<string>("path")
    {
        Description = "Path to the backup zip file."
    };

    var createCommand = new Command("create", "Create a backup archive of the BotNexus home directory.");
    createCommand.Add(outputOption);
    createCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        var outputPath = parseResult.GetValue(outputOption);

        try
        {
            var result = CreateBackupArchive(homePath, outputPath);
            ConsoleOutput.WriteStatus(ConsoleStatus.Success, $"Backup created: {result.ArchivePath}");
            AnsiConsole.MarkupLine($"   [dim]Config:[/] {result.Summary.ConfigFiles} {Pluralize("file", result.Summary.ConfigFiles)} [dim]|[/] " +
                                  $"[dim]Agents:[/] {result.Summary.AgentDirectories} {Pluralize("dir", result.Summary.AgentDirectories)} [dim]|[/] " +
                                  $"[dim]Sessions:[/] {result.Summary.SessionFiles} {Pluralize("file", result.Summary.SessionFiles)} [dim]|[/] " +
                                  $"[dim]Tokens:[/] {result.Summary.TokenFiles} {Pluralize("file", result.Summary.TokenFiles)}");
            AnsiConsole.MarkupLine($"   [dim]Size:[/] {FormatSize(result.ArchiveSizeBytes)}");
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Error, $"Backup failed: {ex.Message}");
            return 1;
        }
    });

    var restoreCommand = new Command("restore", "Restore BotNexus home data from a backup archive.")
    {
        backupPathArgument
    };
    restoreCommand.Add(forceOption);
    restoreCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        var backupPath = parseResult.GetValue(backupPathArgument);
        var force = parseResult.GetValue(forceOption);

        if (string.IsNullOrWhiteSpace(backupPath))
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Error, "Backup path is required.");
            return 1;
        }

        try
        {
            var resolvedBackupPath = Path.GetFullPath(backupPath);
            if (!File.Exists(resolvedBackupPath))
            {
                ConsoleOutput.WriteStatus(ConsoleStatus.Error, $"Backup file not found: {resolvedBackupPath}");
                return 1;
            }

            var backupSummary = InspectBackupArchive(resolvedBackupPath);
            if (!force)
            {
                ConsoleOutput.WriteHeader("Restore Preview");
                AnsiConsole.MarkupLine($"[dim]Archive:[/] {Markup.Escape(resolvedBackupPath)}");
                AnsiConsole.MarkupLine($"[dim]Size:[/] {FormatSize(backupSummary.ArchiveSizeBytes)}");
                AnsiConsole.MarkupLine($"[dim]Files:[/] {backupSummary.TotalFiles}");
                AnsiConsole.MarkupLine($"[dim]Config:[/] {backupSummary.ConfigFiles} {Pluralize("file", backupSummary.ConfigFiles)} [dim]|[/] " +
                                      $"[dim]Agents:[/] {backupSummary.AgentDirectories} {Pluralize("dir", backupSummary.AgentDirectories)} [dim]|[/] " +
                                      $"[dim]Sessions:[/] {backupSummary.SessionFiles} {Pluralize("file", backupSummary.SessionFiles)} [dim]|[/] " +
                                      $"[dim]Tokens:[/] {backupSummary.TokenFiles} {Pluralize("file", backupSummary.TokenFiles)}");
                if (!ConsoleOutput.Confirm("This will overwrite your current BotNexus data. Continue?"))
                {
                    ConsoleOutput.WriteStatus(ConsoleStatus.Warning, "Restore canceled.");
                    return 1;
                }
            }

            var preRestoreBackup = CreateBackupArchive(homePath, null, "botnexus-pre-restore");
            ConsoleOutput.WriteStatus(ConsoleStatus.Success, $"Pre-restore backup created: {preRestoreBackup.ArchivePath}");

            RestoreBackupArchive(resolvedBackupPath, homePath);
            ConsoleOutput.WriteStatus(ConsoleStatus.Success, $"Backup restored: {resolvedBackupPath}");
            var totalFiles = backupSummary.TotalFiles ?? 0;
            AnsiConsole.MarkupLine($"   [dim]Restored:[/] {totalFiles} {Pluralize("file", totalFiles)}");
            AnsiConsole.MarkupLine($"   [dim]Config:[/] {backupSummary.ConfigFiles} {Pluralize("file", backupSummary.ConfigFiles)} [dim]|[/] " +
                                  $"[dim]Agents:[/] {backupSummary.AgentDirectories} {Pluralize("dir", backupSummary.AgentDirectories)} [dim]|[/] " +
                                  $"[dim]Sessions:[/] {backupSummary.SessionFiles} {Pluralize("file", backupSummary.SessionFiles)} [dim]|[/] " +
                                  $"[dim]Tokens:[/] {backupSummary.TokenFiles} {Pluralize("file", backupSummary.TokenFiles)}");
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Error, $"Restore failed: {ex.Message}");
            return 1;
        }
    });

    var listCommand = new Command("list", "List available backup archives.");
    listCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        var backupsPath = ResolveBackupsDirectory(homePath);
        if (!Directory.Exists(backupsPath))
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Warning, "No backups found.");
            return 0;
        }

        var backups = Directory.EnumerateFiles(backupsPath, "*.zip", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();
        if (backups.Count == 0)
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Warning, "No backups found.");
            return 0;
        }

        ConsoleOutput.WriteTable(
            ["Name", "Date", "Size"],
            backups.Select(file => new[]
            {
                file.Name,
                file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                FormatSize(file.Length)
            }));
        return 0;
    });

    backupCommand.Add(createCommand);
    backupCommand.Add(restoreCommand);
    backupCommand.Add(listCommand);
    return backupCommand;
}

static Command BuildInstallCommand(Option<string?> homeOption)
{
    var installPathOption = new Option<string?>("--install-path")
    {
        Description = "Install path. Defaults to %LOCALAPPDATA%\\BotNexus (override with BOTNEXUS_INSTALL env var)."
    };
    var packagesPathOption = new Option<string?>("--packages")
    {
        Description = "Packages folder. Defaults to ./artifacts if present, else ~/.botnexus/packages."
    };

    var installCommand = new Command("install", "Install BotNexus packages into the app directory.");
    installCommand.Add(installPathOption);
    installCommand.Add(packagesPathOption);
    installCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        var installPath = ResolveInstallPath(parseResult.GetValue(installPathOption));
        var packagesPath = ResolvePackagesPath(parseResult.GetValue(packagesPathOption), homePath);

        return RunInstall(homePath, installPath, packagesPath);
    });

    return installCommand;
}

static Command BuildUpdateCommand(Option<string?> homeOption, ConfigFileManager configManager)
{
    var installPathOption = new Option<string?>("--install-path")
    {
        Description = "Install path. Defaults to %LOCALAPPDATA%\\BotNexus (override with BOTNEXUS_INSTALL env var)."
    };
    var packagesPathOption = new Option<string?>("--packages")
    {
        Description = "Packages folder. Defaults to ./artifacts if present, else ~/.botnexus/packages."
    };

    var updateCommand = new Command("update", "Update BotNexus deployment and restart gateway if needed.");
    updateCommand.Add(installPathOption);
    updateCommand.Add(packagesPathOption);
    updateCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        var installPath = ResolveInstallPath(parseResult.GetValue(installPathOption));
        var packagesPath = ResolvePackagesPath(parseResult.GetValue(packagesPathOption), homePath);

        var pidPath = Path.Combine(homePath, "gateway.pid");
        var existingPid = File.Exists(pidPath) ? ReadPid(pidPath) : null;
        var wasRunning = existingPid is not null && IsRunning(existingPid.Value);

        if (wasRunning)
        {
            var stopResult = StopGateway(homePath);
            if (stopResult != 0)
            {
                ConsoleOutput.WriteStatus(ConsoleStatus.Error, "Failed to stop gateway before update.");
                return 1;
            }
        }

        var installResult = RunInstall(homePath, installPath, packagesPath);
        if (installResult != 0)
            return installResult;

        if (wasRunning)
        {
            var gatewayDllPath = Path.Combine(installPath, "gateway", "BotNexus.Gateway.dll");
            if (!File.Exists(gatewayDllPath))
            {
                ConsoleOutput.WriteStatus(ConsoleStatus.Error, $"Gateway DLL not found for restart: {gatewayDllPath}");
                return 1;
            }

            var startResult = StartInstalledGateway(homePath, configManager, gatewayDllPath);
            if (startResult != 0)
                return startResult;
        }

        ConsoleOutput.WriteStatus(ConsoleStatus.Success, wasRunning
            ? "Update complete. Gateway restarted."
            : "Update complete.");
        return 0;
    });

    return updateCommand;
}

static Command BuildStartCommand(Option<string?> homeOption, ConfigFileManager configManager)
{
    var foregroundOption = new Option<bool>("--foreground")
    {
        Description = "Run Gateway in foreground."
    };

    var startCommand = new Command("start", "Start Gateway.");
    startCommand.Add(foregroundOption);
    startCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        var foreground = parseResult.GetValue(foregroundOption);
        return StartGateway(homePath, configManager, foreground);
    });

    return startCommand;
}

static Command BuildStopCommand(Option<string?> homeOption)
{
    var stopCommand = new Command("stop", "Stop Gateway.");
    stopCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        return StopGateway(homePath);
    });
    return stopCommand;
}

static Command BuildRestartCommand(Option<string?> homeOption, ConfigFileManager configManager)
{
    var restartCommand = new Command("restart", "Restart Gateway.");
    restartCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        _ = StopGateway(homePath);
        return StartGateway(homePath, configManager, foreground: false);
    });
    return restartCommand;
}

static int StartGateway(string homePath, ConfigFileManager configManager, bool foreground)
{
    var pidPath = Path.Combine(homePath, "gateway.pid");
    if (!foreground && File.Exists(pidPath))
    {
        var existingPid = ReadPid(pidPath);
        if (existingPid is not null && IsRunning(existingPid.Value))
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Warning, $"Gateway already running (PID {existingPid.Value}).");
            return 1;
        }

        File.Delete(pidPath);
    }

    if (!TryResolveGatewayLaunch(homePath, out var workingDirectory, out var launchArgs))
    {
        ConsoleOutput.WriteStatus(ConsoleStatus.Error,
            "Could not locate BotNexus.Gateway. Install the app or run from a repo with src\\BotNexus.Gateway\\BotNexus.Gateway.csproj.");
        return 1;
    }

    var startInfo = new ProcessStartInfo("dotnet", launchArgs)
    {
        UseShellExecute = false,
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = !foreground,
        RedirectStandardError = !foreground,
        RedirectStandardInput = !foreground,
        CreateNoWindow = !foreground
    };
    startInfo.Environment["BOTNEXUS_HOME"] = homePath;

    var process = Process.Start(startInfo);
    if (process is null)
    {
        ConsoleOutput.WriteStatus(ConsoleStatus.Error, "Failed to start gateway process.");
        return 1;
    }

    if (!foreground)
    {
        process.StandardOutput.Close();
        process.StandardError.Close();
        process.StandardInput.Close();
    }

    if (foreground)
    {
        ConsoleOutput.WriteStatus(ConsoleStatus.Success, "Gateway started in foreground.");
        process.WaitForExit();
        return process.ExitCode == 0 ? 0 : 1;
    }

    Directory.CreateDirectory(homePath);
    File.WriteAllText(pidPath, process.Id.ToString());

    var config = configManager.LoadConfig(homePath);
    var gatewayUrl = BuildGatewayUrl(config.Gateway);
    using var client = new GatewayClient(gatewayUrl);
    var timeoutAt = DateTime.UtcNow.AddSeconds(20);
    while (DateTime.UtcNow < timeoutAt)
    {
        if (process.HasExited)
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Error, "Gateway exited before becoming healthy.");
            return 1;
        }

        if (client.IsRunningAsync().GetAwaiter().GetResult())
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Success, $"Gateway started (PID {process.Id}).");
            return 0;
        }

        Thread.Sleep(500);
    }

    ConsoleOutput.WriteStatus(ConsoleStatus.Warning, $"Gateway process started (PID {process.Id}) but health check timed out.");
    return 1;
}

static int StopGateway(string homePath)
{
    var pidPath = Path.Combine(homePath, "gateway.pid");
    
    // Try PID file first
    if (File.Exists(pidPath))
    {
        var pid = ReadPid(pidPath);
        if (pid is not null && IsRunning(pid.Value))
        {
            KillProcess(pid.Value);
            File.Delete(pidPath);
            ConsoleOutput.WriteStatus(ConsoleStatus.Success, $"Gateway stopped (PID {pid.Value}).");
            return 0;
        }
        
        // PID file exists but process is gone — clean up
        File.Delete(pidPath);
    }
    
    // Fallback: find gateway process by name
    var gatewayProcess = FindGatewayProcess();
    if (gatewayProcess is not null)
    {
        var fallbackPid = gatewayProcess.Id;
        KillProcess(fallbackPid);
        ConsoleOutput.WriteStatus(ConsoleStatus.Success, $"Gateway stopped (PID {fallbackPid}, found by process name).");
        return 0;
    }
    
    ConsoleOutput.WriteStatus(ConsoleStatus.Warning, "Gateway is not running.");
    return 1;
}

static void KillProcess(int pid)
{
    try
    {
        var process = Process.GetProcessById(pid);
        try
        {
            if (!process.CloseMainWindow())
                process.Kill();
        }
        catch
        {
            process.Kill();
        }
        process.WaitForExit(5000);
    }
    catch
    {
        // Process already exited
    }
}

static Process? FindGatewayProcess()
{
    // Try self-contained exe name first
    try
    {
        var candidates = Process.GetProcessesByName("BotNexus.Gateway");
        if (candidates.Length > 0) return candidates[0];
    }
    catch { }
    
    // Can't reliably distinguish dotnet-hosted gateway from other dotnet processes
    return null;
}

static string ResolveHome(ParseResult parseResult, Option<string?> homeOption)
{
    var homePath = parseResult.GetValue(homeOption);
    if (!string.IsNullOrWhiteSpace(homePath))
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", homePath);

    return BotNexusHome.ResolveHomePath();
}

static int? ReadPid(string pidPath)
{
    return int.TryParse(File.ReadAllText(pidPath).Trim(), out var pid) ? pid : null;
}

static bool IsRunning(int pid)
{
    try
    {
        return !Process.GetProcessById(pid).HasExited;
    }
    catch
    {
        return false;
    }
}

static string ResolveInstallPath(string? installPath)
{
    if (!string.IsNullOrWhiteSpace(installPath))
        return Path.GetFullPath(BotNexusHome.ResolvePath(installPath));

    var envOverride = Environment.GetEnvironmentVariable("BOTNEXUS_INSTALL");
    if (!string.IsNullOrWhiteSpace(envOverride))
        return Path.GetFullPath(envOverride);

    var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    return Path.Combine(appData, "BotNexus");
}

static string ResolvePackagesPath(string? packagesPath, string homePath)
{
    if (!string.IsNullOrWhiteSpace(packagesPath))
        return Path.GetFullPath(BotNexusHome.ResolvePath(packagesPath));

    var cwdArtifacts = Path.Combine(Directory.GetCurrentDirectory(), "artifacts");
    if (Directory.Exists(cwdArtifacts))
        return cwdArtifacts;

    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".botnexus",
        "packages");
}

static int RunInstall(string homePath, string installPath, string packagesPath)
{
    if (!Directory.Exists(packagesPath))
    {
        ConsoleOutput.WriteStatus(ConsoleStatus.Error, $"Packages directory not found: {packagesPath}");
        return 1;
    }

    Directory.CreateDirectory(installPath);
    var packageFiles = Directory.EnumerateFiles(packagesPath, "*.nupkg", SearchOption.TopDirectoryOnly)
        .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
        .ToList();
    if (packageFiles.Count == 0)
    {
        ConsoleOutput.WriteStatus(ConsoleStatus.Error, $"No .nupkg files found in {packagesPath}");
        return 1;
    }

    var installed = new List<PackageInstallResult>();
    try
    {
        foreach (var packageFile in packageFiles)
        {
            var packageId = Path.GetFileNameWithoutExtension(packageFile);
            var target = GetInstallTarget(packageId, installPath);
            if (target.Kind == "cli")
            {
                installed.Add(new PackageInstallResult(Path.GetFileName(packageFile), "cli", "(skipped)", "skipped"));
                continue;
            }

            if (Directory.Exists(target.TargetPath))
                Directory.Delete(target.TargetPath, recursive: true);
            Directory.CreateDirectory(target.TargetPath);

            using var archive = ZipFile.OpenRead(packageFile);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name))
                    continue;
                if (IsNuGetMetadataEntry(entry.FullName))
                    continue;

                var entryPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var destinationPath = Path.Combine(target.TargetPath, entryPath);
                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                    Directory.CreateDirectory(destinationDirectory);

                entry.ExtractToFile(destinationPath, overwrite: true);
            }

            installed.Add(new PackageInstallResult(Path.GetFileName(packageFile), target.Kind, target.TargetPath, "installed"));
        }
    }
    catch (Exception ex)
    {
        ConsoleOutput.WriteStatus(ConsoleStatus.Error, $"Install failed: {ex.Message}");
        return 1;
    }

    var versionPath = WriteVersionManifest(installPath, packagesPath, installed.Select(item => item.Package).ToList());
    UpdateExtensionsPathInConfig(homePath, Path.Combine(installPath, "extensions"));

    ConsoleOutput.WriteStatus(ConsoleStatus.Success, $"Installed {installed.Count(item => item.Status == "installed")} package(s).");
    ConsoleOutput.WriteTable(
        ["package", "kind", "status", "target"],
        installed.Select(item => new[] { item.Package, item.Kind, item.Status, item.Target }));
    AnsiConsole.MarkupLine($"[dim]version.json written to {Markup.Escape(versionPath)}[/]");
    return 0;
}

static InstallTarget GetInstallTarget(string packageId, string installRoot)
{
    if (string.Equals(packageId, "BotNexus.Gateway", StringComparison.OrdinalIgnoreCase))
        return new InstallTarget("gateway", Path.Combine(installRoot, "gateway"));
    if (string.Equals(packageId, "BotNexus.Cli", StringComparison.OrdinalIgnoreCase))
        return new InstallTarget("cli", Path.Combine(installRoot, "cli"));

    var extensionMatch = Regex.Match(
        packageId,
        @"^BotNexus\.(?<type>Providers|Channels|Tools)\.(?<name>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    if (extensionMatch.Success)
    {
        var extensionType = extensionMatch.Groups["type"].Value.ToLowerInvariant();
        var extensionName = extensionMatch.Groups["name"].Value.ToLowerInvariant();
        return new InstallTarget("extension", Path.Combine(installRoot, "extensions", extensionType, extensionName));
    }

    throw new InvalidOperationException($"Unknown package naming pattern: {packageId}");
}

static bool IsNuGetMetadataEntry(string entryPath)
{
    var normalized = entryPath.Replace('\\', '/');
    if (string.Equals(normalized, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
        return true;
    if (normalized.StartsWith("_rels/", StringComparison.OrdinalIgnoreCase))
        return true;
    if (normalized.StartsWith("package/", StringComparison.OrdinalIgnoreCase))
        return true;
    if (normalized.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
        return true;
    return false;
}

static string WriteVersionManifest(string installPath, string packagesPath, IReadOnlyCollection<string> packages)
{
    var version = ResolveCliVersion();
    var isRelease = version.Contains("-dev") is false;
    var payload = new
    {
        Version = version,
        InstalledAtUtc = DateTime.UtcNow.ToString("o"),
        Commit = ResolveGitCommitHash(),
        Source = isRelease ? "release" : "dev",
        PackagesPath = packagesPath,
        InstallPath = installPath,
        Packages = packages
    };

    var versionPath = Path.Combine(installPath, "version.json");
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(versionPath, json);
    return versionPath;
}

static string ResolveGitCommitHash()
{
    return TryRunGitCommand("rev-parse --short HEAD", out var output)
        ? output
        : "unknown";
}

static string ResolveCliVersion()
{
    var embeddedVersion = ResolveEmbeddedAssemblyVersion();
    var isDefaultDevVersion = string.Equals(embeddedVersion, "0.0.0-dev", StringComparison.OrdinalIgnoreCase)
        || embeddedVersion.StartsWith("0.0.0-dev+", StringComparison.OrdinalIgnoreCase);
    if (!isDefaultDevVersion)
        return embeddedVersion;

    if (TryResolveGitVersion(out var gitVersion))
        return gitVersion;

    return embeddedVersion;
}

static string ResolveEmbeddedAssemblyVersion()
{
    var assembly = Assembly.GetExecutingAssembly();
    return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";
}

static bool TryResolveGitVersion(out string version)
{
    var envVersion = Environment.GetEnvironmentVariable("BOTNEXUS_VERSION");
    if (!string.IsNullOrWhiteSpace(envVersion))
    {
        version = envVersion.Trim();
        return true;
    }

    if (TryRunGitCommand("describe --tags --exact-match HEAD", out var tag) &&
        tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) &&
        tag.Length > 1)
    {
        version = tag[1..];
        return true;
    }

    if (!TryRunGitCommand("rev-parse --short HEAD", out var hash))
    {
        version = string.Empty;
        return false;
    }

    var dirty = TryRunGitCommand("status --porcelain", out var status) && !string.IsNullOrWhiteSpace(status)
        ? ".dirty"
        : string.Empty;
    version = $"0.0.0-dev.{hash}{dirty}";
    return true;
}

static bool TryRunGitCommand(string arguments, out string output)
{
    output = string.Empty;
    try
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
            return false;

        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        if (process.ExitCode != 0)
            return false;

        output = stdout.Trim();
        return !string.IsNullOrWhiteSpace(output);
    }
    catch
    {
        return false;
    }
}

static InstalledVersionInfo? ReadInstalledVersionInfo(string installPath)
{
    var versionPath = Path.Combine(installPath, "version.json");
    if (!File.Exists(versionPath))
        return null;

    try
    {
        var content = File.ReadAllText(versionPath);
        var root = JsonNode.Parse(content) as JsonObject;
        if (root is null)
            return null;

        var version = root["Version"]?.GetValue<string>() ?? "unknown";
        var installedAtUtc = root["InstalledAtUtc"]?.GetValue<string>();
        var commit = root["Commit"]?.GetValue<string>();
        var source = root["Source"]?.GetValue<string>();
        var packagesPath = root["PackagesPath"]?.GetValue<string>();
        return new InstalledVersionInfo(version, installedAtUtc, commit, source, packagesPath);
    }
    catch
    {
        return null;
    }
}

static void UpdateExtensionsPathInConfig(string homePath, string extensionsPath)
{
    Directory.CreateDirectory(homePath);
    var configPath = Path.Combine(homePath, "config.json");
    JsonObject rootObject;
    if (File.Exists(configPath))
    {
        var content = File.ReadAllText(configPath);
        rootObject = JsonNode.Parse(content) as JsonObject ?? new JsonObject();
    }
    else
    {
        rootObject = new JsonObject();
    }

    if (rootObject["BotNexus"] is not JsonObject botNexusSection)
    {
        botNexusSection = new JsonObject();
        rootObject["BotNexus"] = botNexusSection;
    }

    botNexusSection["ExtensionsPath"] = extensionsPath;
    File.WriteAllText(
        configPath,
        rootObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}

static int StartInstalledGateway(string homePath, ConfigFileManager configManager, string gatewayDllPath)
{
    var workingDirectory = Path.GetDirectoryName(gatewayDllPath) ?? Directory.GetCurrentDirectory();
    var startInfo = new ProcessStartInfo("dotnet", $"\"{gatewayDllPath}\"")
    {
        UseShellExecute = false,
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        CreateNoWindow = true
    };
    startInfo.Environment["BOTNEXUS_HOME"] = homePath;

    var process = Process.Start(startInfo);
    if (process is null)
    {
        ConsoleOutput.WriteStatus(ConsoleStatus.Error, "Failed to restart gateway process.");
        return 1;
    }

    process.StandardOutput.Close();
    process.StandardError.Close();
    process.StandardInput.Close();

    Directory.CreateDirectory(homePath);
    var pidPath = Path.Combine(homePath, "gateway.pid");
    File.WriteAllText(pidPath, process.Id.ToString());

    var config = configManager.LoadConfig(homePath);
    var gatewayUrl = BuildGatewayUrl(config.Gateway);
    using var client = new GatewayClient(gatewayUrl);
    var timeoutAt = DateTime.UtcNow.AddSeconds(20);
    while (DateTime.UtcNow < timeoutAt)
    {
        if (process.HasExited)
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Error, "Gateway exited before becoming healthy after update.");
            return 1;
        }

        if (client.IsRunningAsync().GetAwaiter().GetResult())
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Success, $"Gateway restarted (PID {process.Id}).");
            return 0;
        }

        Thread.Sleep(500);
    }

    ConsoleOutput.WriteStatus(ConsoleStatus.Warning, $"Gateway restarted (PID {process.Id}) but health check timed out.");
    return 1;
}

static bool TryResolveGatewayLaunch(string homePath, out string workingDirectory, out string args)
{
    var installBase = Environment.GetEnvironmentVariable("BOTNEXUS_INSTALL")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotNexus");
    var gatewayDll = Path.Combine(installBase, "gateway", "BotNexus.Gateway.dll");
    if (File.Exists(gatewayDll))
    {
        workingDirectory = Path.GetDirectoryName(gatewayDll) ?? Directory.GetCurrentDirectory();
        args = $"\"{gatewayDll}\"";
        return true;
    }

    foreach (var root in EnumerateSearchRoots())
    {
        var projectPath = Path.Combine(root, "src", "BotNexus.Gateway", "BotNexus.Gateway.csproj");
        if (!File.Exists(projectPath))
            continue;

        workingDirectory = Path.GetDirectoryName(projectPath) ?? root;
        args = $"run --project \"{projectPath}\"";
        return true;
    }

    workingDirectory = string.Empty;
    args = string.Empty;
    return false;
}

static BackupResult CreateBackupArchive(string homePath, string? outputPath, string namePrefix = "botnexus-backup")
{
    if (!Directory.Exists(homePath))
        throw new DirectoryNotFoundException($"Home directory not found: {homePath}");

    var archivePath = string.IsNullOrWhiteSpace(outputPath)
        ? Path.Combine(ResolveBackupsDirectory(homePath), $"{namePrefix}-{DateTime.Now:yyyy-MM-ddTHH-mm-ss}.zip")
        : Path.GetFullPath(outputPath);
    var destinationDirectory = Path.GetDirectoryName(archivePath);
    if (string.IsNullOrWhiteSpace(destinationDirectory))
        throw new InvalidOperationException($"Unable to resolve output directory for: {archivePath}");

    Directory.CreateDirectory(destinationDirectory);

    var summary = BuildBackupSummaryFromHome(homePath);
    if (File.Exists(archivePath))
        File.Delete(archivePath);

    var archiveFullPath = Path.GetFullPath(archivePath);
    using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
    {
        foreach (var filePath in Directory.EnumerateFiles(homePath, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFullPath(filePath), archiveFullPath, StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = Path.GetRelativePath(homePath, filePath);
            if (ShouldExcludeFromBackup(relativePath))
                continue;

            archive.CreateEntryFromFile(filePath, NormalizeArchivePath(relativePath), CompressionLevel.Optimal);
        }
    }

    var size = new FileInfo(archivePath).Length;
    return new BackupResult(archivePath, size, summary);
}

static BackupSummary BuildBackupSummaryFromHome(string homePath)
{
    var configFiles = File.Exists(Path.Combine(homePath, "config.json")) ? 1 : 0;
    var agentDirectories = CountDirectories(Path.Combine(homePath, "agents"));
    var sessionFiles = CountFiles(Path.Combine(homePath, "sessions"));
    var tokenFiles = CountFiles(Path.Combine(homePath, "tokens"));
    var extensionFiles = CountFiles(Path.Combine(homePath, "extensions"));

    return new BackupSummary(configFiles, agentDirectories, sessionFiles, tokenFiles, extensionFiles, null);
}

static BackupSummary InspectBackupArchive(string backupPath)
{
    var configFiles = 0;
    var agentDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var sessionFiles = 0;
    var tokenFiles = 0;
    var extensionFiles = 0;
    var totalFiles = 0;

    using var archive = ZipFile.OpenRead(backupPath);
    foreach (var entry in archive.Entries)
    {
        if (string.IsNullOrEmpty(entry.Name))
            continue;

        totalFiles++;
        var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
        var topSegment = GetTopLevelSegment(relativePath);
        if (relativePath.Equals("config.json", StringComparison.OrdinalIgnoreCase))
            configFiles++;

        if (topSegment.Equals("agents", StringComparison.OrdinalIgnoreCase))
        {
            var remaining = relativePath["agents".Length..].TrimStart(Path.DirectorySeparatorChar);
            var agentName = remaining.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(agentName))
                agentDirectories.Add(agentName);
        }
        else if (topSegment.Equals("sessions", StringComparison.OrdinalIgnoreCase))
        {
            sessionFiles++;
        }
        else if (topSegment.Equals("tokens", StringComparison.OrdinalIgnoreCase))
        {
            tokenFiles++;
        }
        else if (topSegment.Equals("extensions", StringComparison.OrdinalIgnoreCase))
        {
            extensionFiles++;
        }
    }

    var archiveSizeBytes = new FileInfo(backupPath).Length;
    return new BackupSummary(configFiles, agentDirectories.Count, sessionFiles, tokenFiles, extensionFiles, totalFiles)
    {
        ArchiveSizeBytes = archiveSizeBytes
    };
}

static void RestoreBackupArchive(string backupPath, string homePath)
{
    Directory.CreateDirectory(homePath);
    using var archive = ZipFile.OpenRead(backupPath);
    var fullHomePath = Path.GetFullPath(homePath)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    foreach (var entry in archive.Entries)
    {
        var destinationPath = GetSafeDestinationPath(fullHomePath, entry.FullName);
        if (string.IsNullOrEmpty(entry.Name))
        {
            Directory.CreateDirectory(destinationPath);
            continue;
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        entry.ExtractToFile(destinationPath, overwrite: true);
    }
}

static string GetSafeDestinationPath(string fullHomePath, string entryPath)
{
    var normalizedEntryPath = entryPath.Replace('/', Path.DirectorySeparatorChar);
    var destinationPath = Path.GetFullPath(Path.Combine(fullHomePath, normalizedEntryPath));
    if (!destinationPath.StartsWith(fullHomePath, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Backup archive contains an invalid path: {entryPath}");

    return destinationPath;
}

static int CountFiles(string directoryPath)
    => Directory.Exists(directoryPath) ? Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories).Count() : 0;

static int CountDirectories(string directoryPath)
    => Directory.Exists(directoryPath) ? Directory.EnumerateDirectories(directoryPath, "*", SearchOption.TopDirectoryOnly).Count() : 0;

static string ResolveBackupsDirectory(string homePath)
{
    return homePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "-backups";
}

static bool ShouldExcludeFromBackup(string relativePath)
{
    if (string.IsNullOrWhiteSpace(relativePath))
        return false;

    var topSegment = GetTopLevelSegment(relativePath);
    return topSegment.Equals("logs", StringComparison.OrdinalIgnoreCase);
}

static string GetTopLevelSegment(string relativePath)
{
    var segments = relativePath.Split(
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
        StringSplitOptions.RemoveEmptyEntries);
    return segments.Length > 0 ? segments[0] : string.Empty;
}

static string NormalizeArchivePath(string path)
    => path.Replace(Path.DirectorySeparatorChar, '/');

static string Pluralize(string noun, int count)
    => count == 1 ? noun : $"{noun}s";

static string FormatSize(long bytes)
{
    const double kiloByte = 1024d;
    const double megaByte = kiloByte * 1024d;
    const double gigaByte = megaByte * 1024d;

    if (bytes < kiloByte)
        return $"{bytes} B";
    if (bytes < megaByte)
        return $"{Math.Round(bytes / kiloByte)} KB";
    if (bytes < gigaByte)
        return $"{Math.Round(bytes / megaByte, 1)} MB";

    return $"{Math.Round(bytes / gigaByte, 1)} GB";
}

static IEnumerable<string> EnumerateSearchRoots()
{
    var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var path = Path.GetFullPath(seed);
        while (!string.IsNullOrWhiteSpace(path))
        {
            if (visited.Add(path))
                yield return path;

            var parent = Directory.GetParent(path);
            if (parent is null)
                break;

            path = parent.FullName;
        }
    }
}

static string BuildGatewayUrl(GatewayConfig gateway)
{
    var host = gateway.Host;
    if (string.IsNullOrWhiteSpace(host) || host == "0.0.0.0" || host == "::")
        host = "localhost";

    return $"http://{host}:{gateway.Port}";
}

readonly record struct BackupResult(string ArchivePath, long ArchiveSizeBytes, BackupSummary Summary);
readonly record struct InstallTarget(string Kind, string TargetPath);
readonly record struct PackageInstallResult(string Package, string Kind, string Target, string Status);
readonly record struct InstalledVersionInfo(string Version, string? InstalledAtUtc, string? Commit, string? Source, string? PackagesPath);

readonly record struct BackupSummary(
    int ConfigFiles,
    int AgentDirectories,
    int SessionFiles,
    int TokenFiles,
    int ExtensionFiles,
    int? TotalFiles)
{
    public long ArchiveSizeBytes { get; init; }
}
