using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

internal sealed class InitCommand
{
    public Command Build(Option<bool> verboseOption)
    {
        var forceOption = new Option<bool>("--force", "Overwrite existing config.json.");
        var command = new Command("init", "Initialize ~/.botnexus with a default config and required directories.")
        {
            forceOption
        };

        command.SetHandler(async context =>
        {
            var force = context.ParseResult.GetValueForOption(forceOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteAsync(force, verbose, CancellationToken.None);
        });

        return command;
    }

    public async Task<int> ExecuteAsync(bool force, bool verbose, CancellationToken cancellationToken)
    {
        var homePath = PlatformConfigLoader.DefaultHomePath;
        var configPath = PlatformConfigLoader.DefaultConfigPath;
        PlatformConfigLoader.EnsureConfigDirectory(homePath);

        if (File.Exists(configPath) && !force)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] Config already exists at [dim]{Markup.Escape(configPath)}[/]. Use [green]--force[/] to overwrite.");
            AnsiConsole.MarkupLine($"BotNexus home: [dim]{Markup.Escape(homePath)}[/]");
            return 0;
        }

        var defaultConfig = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                ListenUrl = "http://localhost:5005",
                DefaultAgentId = "assistant"
            },
            Cron = new CronConfig
            {
                Enabled = true,
                TickIntervalSeconds = 60
            },
            Agents = new Dictionary<string, AgentDefinitionConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["assistant"] = new()
                {
                    Provider = "github-copilot",
                    Model = "gpt-4.1",
                    Enabled = true
                }
            }
        };

        await WriteConfigAsync(defaultConfig, configPath, cancellationToken);
        AnsiConsole.MarkupLine($"[green]\u2713[/] Initialized BotNexus home at: [dim]{Markup.Escape(homePath)}[/]");
        AnsiConsole.MarkupLine($"[green]\u2713[/] Created config: [dim]{Markup.Escape(configPath)}[/]");
        AnsiConsole.MarkupLine("\nNext steps:");
        AnsiConsole.MarkupLine("  [green]botnexus provider setup[/]");
        AnsiConsole.MarkupLine("  [green]botnexus validate[/]");
        AnsiConsole.MarkupLine("  [green]botnexus agent list[/]");

        if (verbose)
            AnsiConsole.WriteLine(JsonSerializer.Serialize(defaultConfig, CreateWriteJsonOptions()));

        return 0;
    }

    private static async Task WriteConfigAsync(PlatformConfig config, string configPath, CancellationToken cancellationToken)
    {
        PlatformConfigLoader.EnsureConfigDirectory(PlatformConfigLoader.DefaultHomePath);
        var json = SerializeWithAgentDefaults(config);
        await File.WriteAllTextAsync(configPath, json, cancellationToken);
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
                }
            };
            // Insert defaults as the first key
            var newAgents = new System.Text.Json.Nodes.JsonObject { ["defaults"] = defaultsBlock };
            foreach (var kv in agentsNode)
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
