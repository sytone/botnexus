using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Commands;

internal sealed class AgentCommands
{
    public Command Build(Option<bool> verboseOption)
    {
        var command = new Command("agent", "Manage configured agents.");

        var listCommand = new Command("list", "List configured agents.");
        listCommand.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteListAsync(verbose, CancellationToken.None);
        });

        var idArgument = new Argument<string>("id", "Agent ID.");
        var providerOption = new Option<string>("--provider", () => "copilot", "Agent provider name.");
        var modelOption = new Option<string>("--model", () => "gpt-4.1", "Agent model name.");
        var enabledOption = new Option<bool>("--enabled", () => true, "Whether the agent is enabled.");

        var addCommand = new Command("add", "Add an agent to config.json.")
        {
            idArgument,
            providerOption,
            modelOption,
            enabledOption
        };
        addCommand.SetHandler(async context =>
        {
            var id = context.ParseResult.GetValueForArgument(idArgument);
            var provider = context.ParseResult.GetValueForOption(providerOption) ?? "copilot";
            var model = context.ParseResult.GetValueForOption(modelOption) ?? "gpt-4.1";
            var enabled = context.ParseResult.GetValueForOption(enabledOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteAddAsync(id, provider, model, enabled, verbose, CancellationToken.None);
        });

        var removeCommand = new Command("remove", "Remove an agent from config.json.")
        {
            idArgument
        };
        removeCommand.SetHandler(async context =>
        {
            var id = context.ParseResult.GetValueForArgument(idArgument);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteRemoveAsync(id, verbose, CancellationToken.None);
        });

        command.AddCommand(listCommand);
        command.AddCommand(addCommand);
        command.AddCommand(removeCommand);
        return command;
    }

    public async Task<int> ExecuteListAsync(bool verbose, CancellationToken cancellationToken)
    {
        var config = await LoadConfigRequiredAsync(cancellationToken);
        if (config is null)
            return 1;

        if (config.Agents is null || config.Agents.Count == 0)
        {
            Console.WriteLine("Agents:");
            Console.WriteLine("  (none)");
            return 0;
        }

        Console.WriteLine("Agents:");
        foreach (var (agentId, agent) in config.Agents.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  {agentId}  provider={agent.Provider ?? "(unset)"}  model={agent.Model ?? "(unset)"}  enabled={agent.Enabled.ToString().ToLowerInvariant()}");
        }

        if (verbose)
            Console.WriteLine($"Loaded from: {PlatformConfigLoader.DefaultConfigPath}");

        return 0;
    }

    public async Task<int> ExecuteAddAsync(string id, string provider, string model, bool enabled, bool verbose, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            Console.WriteLine("Error: agent ID is required.");
            return 1;
        }

        var config = await LoadConfigRequiredAsync(cancellationToken);
        if (config is null)
            return 1;

        config.Agents ??= new Dictionary<string, AgentDefinitionConfig>(StringComparer.OrdinalIgnoreCase);
        if (ContainsDictionaryKey(config.Agents, id))
        {
            Console.WriteLine($"Error: agent '{id}' already exists.");
            return 1;
        }

        config.Agents[id] = new AgentDefinitionConfig
        {
            Provider = provider,
            Model = model,
            Enabled = enabled
        };

        var saveCode = await SaveAndValidateAsync(config, verbose, cancellationToken);
        if (saveCode != 0)
            return saveCode;

        Console.WriteLine($"Added agent '{id}'.");
        return 0;
    }

    public async Task<int> ExecuteRemoveAsync(string id, bool verbose, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            Console.WriteLine("Error: agent ID is required.");
            return 1;
        }

        var config = await LoadConfigRequiredAsync(cancellationToken);
        if (config is null)
            return 1;

        if (config.Agents is null || !TryFindDictionaryKey(config.Agents, id, out var matchedId))
        {
            Console.WriteLine($"Error: agent '{id}' was not found.");
            return 1;
        }

        var defaultAgent = config.Gateway?.DefaultAgentId;
        if (!string.IsNullOrWhiteSpace(defaultAgent) &&
            string.Equals(defaultAgent, matchedId, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Warning: removing default agent '{matchedId}'. Update gateway.defaultAgentId if needed.");
        }

        config.Agents.Remove(matchedId);
        var saveCode = await SaveAndValidateAsync(config, verbose, cancellationToken);
        if (saveCode != 0)
            return saveCode;

        Console.WriteLine($"Removed agent '{matchedId}'.");
        return 0;
    }

    private static async Task<PlatformConfig?> LoadConfigRequiredAsync(CancellationToken cancellationToken)
    {
        var configPath = PlatformConfigLoader.DefaultConfigPath;
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"Error: config file not found at '{configPath}'. Run 'botnexus init' first.");
            return null;
        }

        try
        {
            return await PlatformConfigLoader.LoadAsync(configPath, cancellationToken, validateOnLoad: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: unable to load config: {ex.Message}");
            return null;
        }
    }

    private static async Task<int> SaveAndValidateAsync(PlatformConfig config, bool verbose, CancellationToken cancellationToken)
    {
        var configPath = PlatformConfigLoader.DefaultConfigPath;
        await WriteConfigAsync(config, configPath, cancellationToken);

        var reloaded = await PlatformConfigLoader.LoadAsync(configPath, cancellationToken, validateOnLoad: false);
        var errors = PlatformConfigLoader.Validate(reloaded);
        if (errors.Count > 0)
        {
            Console.WriteLine("Config validation failed after write:");
            foreach (var error in errors)
                Console.WriteLine($"- {error}");
            return 1;
        }

        if (verbose)
            Console.WriteLine($"Saved config: {configPath}");

        return 0;
    }

    private static async Task WriteConfigAsync(PlatformConfig config, string configPath, CancellationToken cancellationToken)
    {
        PlatformConfigLoader.EnsureConfigDirectory(PlatformConfigLoader.DefaultHomePath);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, CreateWriteJsonOptions()), cancellationToken);
    }

    private static bool ContainsDictionaryKey<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key)
        where TKey : notnull
    {
        if (dictionary.ContainsKey(key))
            return true;

        if (key is string stringKey)
            return dictionary.Keys.OfType<string>().Any(k => string.Equals(k, stringKey, StringComparison.OrdinalIgnoreCase));

        return false;
    }

    private static bool TryFindDictionaryKey<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, out TKey matchedKey)
        where TKey : notnull
    {
        if (dictionary.ContainsKey(key))
        {
            matchedKey = key;
            return true;
        }

        if (key is string stringKey)
        {
            foreach (var existingKey in dictionary.Keys)
            {
                if (existingKey is string existingString &&
                    string.Equals(existingString, stringKey, StringComparison.OrdinalIgnoreCase))
                {
                    matchedKey = existingKey;
                    return true;
                }
            }
        }

        matchedKey = default!;
        return false;
    }

    private static JsonSerializerOptions CreateWriteJsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
