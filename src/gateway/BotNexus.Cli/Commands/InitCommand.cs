using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

internal sealed class InitCommand
{
    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var forceOption = new Option<bool>("--force", "Overwrite existing config.json.");

        // Issue #2798: binding every interface is an operator decision, so it is a stated flag
        // rather than the generated default. The flag exists precisely so the choice appears in the
        // command the operator ran, instead of being inherited silently from a generated file.
        var listenAllOption = new Option<bool>(
            "--listen-all-interfaces",
            $"Bind the gateway to every network interface ({GatewayBindAddress.WildcardListenUrl}) for remote or mesh access. "
            + $"This exposes {GatewayBindAddress.ExposedSurfaceDescription} to every reachable network.");

        var command = new Command("init", "Initialize ~/.botnexus with a default config and required directories.")
        {
            forceOption,
            listenAllOption
        };

        command.SetHandler(async context =>
        {
            var force = context.ParseResult.GetValueForOption(forceOption);
            var listenAll = context.ParseResult.GetValueForOption(listenAllOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = await ExecuteAsync(home, force, listenAll, verbose, CancellationToken.None);
        });

        return command;
    }

    public async Task<int> ExecuteAsync(bool force, bool verbose, CancellationToken cancellationToken)
        => await ExecuteAsync(PlatformConfigLoader.DefaultHomePath, force, listenAllInterfaces: false, verbose, cancellationToken);

    public async Task<int> ExecuteAsync(string homePath, bool force, bool verbose, CancellationToken cancellationToken)
        => await ExecuteAsync(homePath, force, listenAllInterfaces: false, verbose, cancellationToken);

    /// <summary>
    /// Generates a fresh <c>config.json</c>. Issue #2798: <paramref name="listenAllInterfaces"/>
    /// defaults to <c>false</c>, so a fresh install binds loopback only and an operator opts into
    /// the wildcard bind explicitly. This affects the GENERATED default for new installs only - an
    /// existing config is never rewritten here unless <paramref name="force"/> was passed.
    /// </summary>
    public async Task<int> ExecuteAsync(string homePath, bool force, bool listenAllInterfaces, bool verbose, CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(homePath, "config.json");
        PlatformConfigLoader.EnsureConfigDirectory(homePath);

        if (File.Exists(configPath) && !force)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠[/] Config already exists at [dim]{Markup.Escape(configPath)}[/]. Use [green]--force[/] to overwrite.");
            AnsiConsole.MarkupLine($"  Home: [dim]{Markup.Escape(homePath)}[/]");
            return 0;
        }

        var interactive = AnsiConsole.Profile.Capabilities.Interactive;
        if (interactive)
        {
            AnsiConsole.Write(new FigletText("BotNexus").Color(Color.Blue));
        }

        var defaultConfig = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                // #2798: loopback by default; the wildcard is reachable only via --listen-all-interfaces.
                // Both literals live on GatewayBindAddress so the doctor advisory that reports a
                // wildcard bind and the value init can emit can never disagree about what a wildcard is.
                ListenUrl = listenAllInterfaces
                    ? GatewayBindAddress.WildcardListenUrl
                    : GatewayBindAddress.LoopbackListenUrl,
                DefaultAgentId = FreshInstallAgentDefaults.DefaultAgentId,
                SessionStore = new SessionStoreConfig
                {
                    Type = "Sqlite",
                    ConnectionString = $"Data Source={Path.Combine(homePath, "sessions.sqlite")}"
                },
                Extensions = new ExtensionsConfig
                {
                    Enabled = true,
                    Defaults = new Dictionary<string, JsonElement>
                    {
                        ["botnexus-skills"] = JsonDocument.Parse("{\"enabled\":true}").RootElement.Clone()
                    }
                }
            },
            Cron = new CronConfig
            {
                Enabled = true,
                TickIntervalSeconds = 60
            },
            // #2636: the generic assistant's provider/model comes from the same shared
            // fresh-install source the bundled agents (below) are built from, so the two can
            // never drift apart.
            Agents = new Dictionary<string, AgentDefinitionConfig>(StringComparer.OrdinalIgnoreCase)
            {
                [FreshInstallAgentDefaults.DefaultAgentId] = new()
                {
                    Provider = FreshInstallAgentDefaults.DefaultProvider,
                    Model = FreshInstallAgentDefaults.DefaultModel,
                    Enabled = true
                }
            }
        };

        await WriteConfigAsync(defaultConfig, configPath, cancellationToken);

        var interactive2 = AnsiConsole.Profile.Capabilities.Interactive;
        if (interactive2)
        {
            var panel = new Panel(
                $"[green]\u2713[/] Initialized BotNexus home\n\n" +
                $"[dim]Home:[/]   [dim]{Markup.Escape(homePath)}[/]\n" +
                $"[dim]Config:[/] [dim]{Markup.Escape(configPath)}[/]\n\n" +
                "[bold]Next steps:[/]\n" +
                "  [green]botnexus provider setup[/]\n" +
                "  [green]botnexus validate[/]\n" +
                "  [green]botnexus agent list[/]")
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader("[bold blue] BotNexus Init [/]"),
                Padding = new Padding(1, 0)
            };
            AnsiConsole.WriteLine();
            AnsiConsole.Write(panel);
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]\u2713[/] Initialized BotNexus home at: [dim]{Markup.Escape(homePath)}[/]");
            AnsiConsole.MarkupLine($"[green]\u2713[/] Created config: [dim]{Markup.Escape(configPath)}[/]");
            AnsiConsole.MarkupLine("\nNext steps:");
            AnsiConsole.MarkupLine("  [green]botnexus provider setup[/]");
            AnsiConsole.MarkupLine("  [green]botnexus validate[/]");
            AnsiConsole.MarkupLine("  [green]botnexus agent list[/]");
        }

        if (verbose)
            AnsiConsole.WriteLine(JsonSerializer.Serialize(defaultConfig, CreateWriteJsonOptions()));

        return 0;
    }

    private static async Task WriteConfigAsync(PlatformConfig config, string configPath, CancellationToken cancellationToken)
    {
        PlatformConfigLoader.EnsureConfigDirectory(Path.GetDirectoryName(configPath) ?? PlatformConfigLoader.DefaultHomePath);
        var json = SerializeWithAgentDefaults(config);
        var fileSystem = new System.IO.Abstractions.FileSystem();
        // #2636 AC6: backups belong under the writable data directory BOTNEXUS_DATA_DIR
        // designates, exactly as PlatformAgentReconciliationService resolves it - not blindly
        // beside config.json, which may be a read-only mount.
        var homeRoot = Path.GetDirectoryName(configPath) ?? BotNexusHome.ResolveHomePath();
        var backupsDir = PlatformAgentReconciliationService.ResolveBackupDirectory(
            new BotNexusHome(fileSystem, homeRoot));
        var writer = new PlatformConfigWriter(configPath, fileSystem, new ConfigBackupService(backupsDir, fileSystem));
        await writer.MutateAsync(root =>
        {
            var replacement = System.Text.Json.Nodes.JsonNode.Parse(json)?.AsObject() ?? new System.Text.Json.Nodes.JsonObject();
            root.Clear();
            foreach (var kvp in replacement)
                root[kvp.Key] = kvp.Value?.DeepClone();
        }, "before-init-write", cancellationToken);
    }

    /// <summary>
    /// Serializes the config to JSON and injects the <c>agents.defaults</c> block
    /// into the agents dictionary, since <see cref="PlatformConfig.AgentDefaults" /> is
    /// a computed/non-serialized property.
    /// </summary>
    private static string SerializeWithAgentDefaults(PlatformConfig config)
    {
        var opts = CreateWriteJsonOptions();
        var json = JsonSerializer.Serialize(config, opts);

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();

        // Inject agents.defaults block if agents object exists
        var agentsNode = root["agents"] as System.Text.Json.Nodes.JsonObject;
        if (agentsNode is not null)
        {
            var defaultsBlock = new System.Text.Json.Nodes.JsonObject
            {
                ["memory"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["enabled"] = true,
                    ["indexing"] = "auto"
                },
                ["heartbeat"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["enabled"] = true,
                    ["intervalMinutes"] = 30,
                    ["quietHours"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["enabled"] = true,
                        ["start"] = "23:00",
                        ["end"] = "07:00"
                    }
                }
            };
            // Insert defaults as the first key
            var newAgents = new System.Text.Json.Nodes.JsonObject { ["defaults"] = defaultsBlock };
            foreach (var kv in agentsNode)
                newAgents[kv.Key] = kv.Value?.DeepClone();

            // #2636: emit the bundled platform agents (Nexus Trailguide) as complete, editable
            // entries produced by the same builder the startup reconciler uses. A fresh install
            // must look finished in config.json without waiting for a startup pass, and the
            // reconciler must then find nothing to do.
            foreach (var kv in FreshInstallAgentDefaults.CreateBundledAgents())
                newAgents[kv.Key] = kv.Value?.DeepClone();

            root["agents"] = newAgents;
        }

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonSerializerOptions CreateWriteJsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
