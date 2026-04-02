using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using BotNexus.Cli.Services;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Diagnostics;
using BotNexus.Diagnostics.Checkups.Configuration;

var homeOption = new Option<string?>("--home")
{
    Description = "Override BOTNEXUS_HOME for this command."
};
homeOption.Recursive = true;

var configManager = new ConfigFileManager();
var rootCommand = new RootCommand("BotNexus CLI");
rootCommand.Add(homeOption);

rootCommand.Add(BuildConfigCommand(homeOption, configManager));
rootCommand.Add(BuildAgentCommand(homeOption, configManager));
rootCommand.Add(BuildProviderCommand(homeOption, configManager));
rootCommand.Add(BuildChannelCommand(homeOption, configManager));
rootCommand.Add(BuildExtensionCommand(homeOption, configManager));
rootCommand.Add(BuildDoctorCommand(homeOption));
rootCommand.Add(BuildStatusCommand(homeOption, configManager));
rootCommand.Add(BuildLogsCommand(homeOption));
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
            var overwrite = Prompt("config.json already exists. Overwrite? (y/N)", "N");
            if (!overwrite.Equals("y", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleOutput.WriteStatus(ConsoleStatus.Warning, "Canceled.");
                return 1;
            }
        }

        var config = new BotNexusConfig();
        var providerName = Prompt("Provider", "copilot");
        var model = Prompt("Model", config.Agents.Model);
        var portText = Prompt("Gateway port", config.Gateway.Port.ToString());
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
        var name = Prompt("Agent name", string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Error, "Agent name is required.");
            return 1;
        }

        var provider = Prompt("Provider", config.Providers.Keys.FirstOrDefault() ?? "copilot");
        var model = Prompt("Model", config.Agents.Model);
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
        var name = Prompt("Provider name", string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Error, "Provider name is required.");
            return 1;
        }

        var auth = Prompt("Auth type", "apikey");
        var apiBase = Prompt("API base", string.Empty);
        var defaultModel = Prompt("Default model", string.Empty);

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
        var type = Prompt("Channel type (discord/slack/telegram)", string.Empty);
        if (!new[] { "discord", "slack", "telegram" }.Contains(type, StringComparer.OrdinalIgnoreCase))
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Error, "Channel type must be discord, slack, or telegram.");
            return 1;
        }

        var token = Prompt("Bot token", string.Empty);
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
        for (var index = 0; index < selectedCheckups.Count && index < results.Count; index++)
        {
            var checkup = selectedCheckups[index];
            var result = results[index];
            var statusText = result.Status switch
            {
                CheckupStatus.Pass => "PASS",
                CheckupStatus.Warn => "WARN",
                _ => "FAIL"
            };

            switch (result.Status)
            {
                case CheckupStatus.Pass:
                    passCount++;
                    break;
                case CheckupStatus.Warn:
                    warnCount++;
                    break;
                default:
                    failCount++;
                    break;
            }

            Console.WriteLine($"[{statusText}] {checkup.Category}/{checkup.Name}: {result.Message}");
            if (!string.IsNullOrWhiteSpace(result.Advice))
                Console.WriteLine($"  -> {result.Advice}");
        }

        Console.WriteLine();
        Console.WriteLine($"Summary: {passCount} passed, {warnCount} warnings, {failCount} failures");
        return failCount > 0 ? 1 : 0;
    });

    return doctorCommand;
}

static Command BuildStatusCommand(Option<string?> homeOption, ConfigFileManager configManager)
{
    var statusCommand = new Command("status", "Show Gateway and configuration status.");
    statusCommand.SetAction(parseResult =>
    {
        var homePath = ResolveHome(parseResult, homeOption);
        var config = configManager.LoadConfig(homePath);
        var gatewayUrl = BuildGatewayUrl(config.Gateway);

        using var client = new GatewayClient(gatewayUrl);
        try
        {
            var health = client.GetHealthAsync().GetAwaiter().GetResult();
            var extensions = client.GetExtensionsAsync().GetAwaiter().GetResult();
            ConsoleOutput.WriteStatus(ConsoleStatus.Success, $"Gateway online ({gatewayUrl})");
            ConsoleOutput.WriteHeader("Health");
            ConsoleOutput.WriteJson(health);
            ConsoleOutput.WriteHeader("Extensions");
            ConsoleOutput.WriteJson(extensions);
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or HttpRequestException)
        {
            ConsoleOutput.WriteStatus(ConsoleStatus.Warning, $"Gateway offline ({gatewayUrl})");
            ConsoleOutput.WriteTable(
                ["setting", "value"],
                [
                    ["gateway host", config.Gateway.Host],
                    ["gateway port", config.Gateway.Port.ToString()],
                    ["named agents", config.Agents.Named.Count.ToString()],
                    ["providers", config.Providers.Count.ToString()],
                ["channels", config.Channels.Instances.Count.ToString()]
                ]);
            return 0;
        }
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

    if (!TryResolveGatewayLaunch(out var workingDirectory, out var launchArgs))
    {
        ConsoleOutput.WriteStatus(ConsoleStatus.Error,
            "Could not locate BotNexus.Gateway. Ensure src\\BotNexus.Gateway\\BotNexus.Gateway.csproj is available.");
        return 1;
    }

    var startInfo = new ProcessStartInfo("dotnet", launchArgs)
    {
        UseShellExecute = false,
        WorkingDirectory = workingDirectory
    };
    startInfo.Environment["BOTNEXUS_HOME"] = homePath;

    var process = Process.Start(startInfo);
    if (process is null)
    {
        ConsoleOutput.WriteStatus(ConsoleStatus.Error, "Failed to start gateway process.");
        return 1;
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
    if (!File.Exists(pidPath))
    {
        ConsoleOutput.WriteStatus(ConsoleStatus.Warning, "Gateway is not running (PID file missing).");
        return 1;
    }

    var pid = ReadPid(pidPath);
    if (pid is null)
    {
        File.Delete(pidPath);
        ConsoleOutput.WriteStatus(ConsoleStatus.Error, "PID file is invalid.");
        return 1;
    }

    if (!IsRunning(pid.Value))
    {
        File.Delete(pidPath);
        ConsoleOutput.WriteStatus(ConsoleStatus.Warning, "Gateway is not running.");
        return 1;
    }

    var process = Process.GetProcessById(pid.Value);
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
    File.Delete(pidPath);
    ConsoleOutput.WriteStatus(ConsoleStatus.Success, $"Gateway stopped (PID {pid.Value}).");
    return 0;
}

static string ResolveHome(ParseResult parseResult, Option<string?> homeOption)
{
    var homePath = parseResult.GetValue(homeOption);
    if (!string.IsNullOrWhiteSpace(homePath))
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", homePath);

    return BotNexusHome.ResolveHomePath();
}

static string Prompt(string label, string defaultValue)
{
    Console.Write($"{label}{(string.IsNullOrWhiteSpace(defaultValue) ? string.Empty : $" [{defaultValue}]")}: ");
    var value = Console.ReadLine();
    return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
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

static bool TryResolveGatewayLaunch(out string workingDirectory, out string args)
{
    var gatewayDll = Path.Combine(AppContext.BaseDirectory, "BotNexus.Gateway.dll");
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
