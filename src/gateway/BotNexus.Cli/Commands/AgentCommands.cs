using System.CommandLine;
using BotNexus.Cli.Wizard;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using Spectre.Console;
using ValidationResult = Spectre.Console.ValidationResult;

namespace BotNexus.Cli.Commands;

internal sealed class AgentCommands
{
    public Command Build(Option<bool> verboseOption)
    {
        var command = new Command("agent", "Manage configured agents.");
        command.AddAlias("agents");

        var listCommand = new Command("list", "List configured agents.");
        var listTargetOption = new Option<string?>("--target", () => null, "BotNexus home directory (config, workspace, extensions). Defaults to ~/.botnexus.");
        listCommand.Add(listTargetOption);
        listCommand.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(listTargetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteListAsync(configPath, verbose, CancellationToken.None);
        });

        var idArgument = new Argument<string>("id", "Agent ID.");
        var providerOption = new Option<string>("--provider", () => "github-copilot", "Agent provider name.");
        var modelOption = new Option<string>("--model", () => "gpt-4.1", "Agent model name.");
        var enabledOption = new Option<bool>("--enabled", () => true, "Whether the agent is enabled.");
        var displayNameOption = new Option<string?>("--display-name", () => null, "Human-readable display name (defaults to agent ID if not set).");
        var descriptionOption = new Option<string?>("--description", () => null, "Description of the agent's purpose.");
        var emojiOption = new Option<string?>("--emoji", () => null, "Emoji shown alongside the agent name in clients (e.g. \"🤖\").");
        var disabledFlag = new Option<bool>("--disabled", () => false, "Disable the agent (sets Enabled = false). Takes precedence over --enabled.");

        var addTargetOption = new Option<string?>("--target", () => null, "BotNexus home directory (config, workspace, extensions). Defaults to ~/.botnexus.");
        var addCommand = new Command("add", "Add an agent to config.json.")
        {
            idArgument,
            providerOption,
            modelOption,
            enabledOption,
            displayNameOption,
            descriptionOption,
            emojiOption,
            disabledFlag,
            addTargetOption
        };
        addCommand.SetHandler(async context =>
        {
            var id = context.ParseResult.GetValueForArgument(idArgument);
            var provider = context.ParseResult.GetValueForOption(providerOption) ?? "github-copilot";
            var model = context.ParseResult.GetValueForOption(modelOption) ?? "gpt-4.1";
            var enabled = context.ParseResult.GetValueForOption(enabledOption);
            var disabled = context.ParseResult.GetValueForOption(disabledFlag);
            var displayName = context.ParseResult.GetValueForOption(displayNameOption);
            var description = context.ParseResult.GetValueForOption(descriptionOption);
            var emoji = context.ParseResult.GetValueForOption(emojiOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(addTargetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            // --disabled flag takes precedence over --enabled
            if (disabled) enabled = false;
            context.ExitCode = await ExecuteAddAsync(id, provider, model, enabled, displayName, description, emoji, configPath, verbose, CancellationToken.None);
        });

        var removeTargetOption = new Option<string?>("--target", () => null, "BotNexus home directory (config, workspace, extensions). Defaults to ~/.botnexus.");
        var removeCommand = new Command("remove", "Remove an agent from config.json.")
        {
            idArgument,
            removeTargetOption
        };
        removeCommand.SetHandler(async context =>
        {
            var id = context.ParseResult.GetValueForArgument(idArgument);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(removeTargetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteRemoveAsync(id, configPath, verbose, CancellationToken.None);
        });

        var wizardTargetOption = new Option<string?>("--target", () => null, "BotNexus home directory (config, workspace, extensions). Defaults to ~/.botnexus.");
        var wizardCommand = new Command("wizard", "Interactively create a new agent using a step-by-step wizard.")
        {
            wizardTargetOption
        };
        wizardCommand.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(wizardTargetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteWizardAsync(configPath, verbose, CancellationToken.None);
        });

        var showIdArgument = new Argument<string>("id", "Agent ID.");
        var showTargetOption = new Option<string?>("--target", () => null, "BotNexus home directory (config, workspace, extensions). Defaults to ~/.botnexus.");
        var showJsonOption = new Option<bool>("--json", "Emit raw JSON instead of a formatted table.");
        var showCommand = new Command("show", "Show the resolved configuration for a single agent.")
        {
            showIdArgument,
            showTargetOption,
            showJsonOption
        };
        showCommand.SetHandler(async context =>
        {
            var id = context.ParseResult.GetValueForArgument(showIdArgument);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(showTargetOption);
            var asJson = context.ParseResult.GetValueForOption(showJsonOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteShowAsync(id, configPath, asJson, verbose, CancellationToken.None);
        });

        command.AddCommand(listCommand);
        command.AddCommand(addCommand);
        command.AddCommand(removeCommand);
        command.AddCommand(wizardCommand);
        command.AddCommand(showCommand);
        return command;
    }

    public async Task<int> ExecuteListAsync(bool verbose, CancellationToken cancellationToken)
        => await ExecuteListAsync(PlatformConfigLoader.DefaultConfigPath, verbose, cancellationToken);

    public async Task<int> ExecuteListAsync(string configPath, bool verbose, CancellationToken cancellationToken)
    {
        var config = await LoadConfigRequiredAsync(configPath, cancellationToken);
        if (config is null)
            return 1;

        if (config.Agents is null || config.Agents.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No agents configured.[/] Run [green]botnexus agent add <id>[/] to create one.");
            return 0;
        }

        var table = new Table()
            .AddColumn("Agent")
            .AddColumn("Provider")
            .AddColumn("Model")
            .AddColumn("Enabled");

        foreach (var (agentId, agent) in config.Agents.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
        {
            table.AddRow(
                Markup.Escape(agentId),
                agent.Provider ?? "[dim](unset)[/]",
                agent.Model ?? "[dim](unset)[/]",
                agent.Enabled ? "[green]Yes[/]" : "[red]No[/]");
        }

        AnsiConsole.Write(table);

        if (verbose)
            AnsiConsole.MarkupLine($"[dim]Loaded from: {Markup.Escape(configPath)}[/]");

        return 0;
    }

    public async Task<int> ExecuteWizardAsync(bool verbose, CancellationToken cancellationToken)
        => await ExecuteWizardAsync(PlatformConfigLoader.DefaultConfigPath, verbose, cancellationToken);

    public async Task<int> ExecuteWizardAsync(string configPath, bool verbose, CancellationToken cancellationToken)
    {
        var config = await LoadConfigRequiredAsync(configPath, cancellationToken);
        if (config is null)
            return 1;

        AnsiConsole.MarkupLine("[bold cyan]BotNexus — Add Agent Wizard[/]");
        AnsiConsole.WriteLine();

        var providerChoices = config.Providers is { Count: > 0 }
            ? config.Providers.Keys.OrderBy(k => k).ToList()
            : ["copilot"];

        var existingIds = (config.Agents?.Keys ?? Enumerable.Empty<string>())
            .Select(k => k.ToLowerInvariant())
            .ToHashSet();

        var wizard = new WizardBuilder()
            .AskText(
                "AgentId",
                "Agent ID [dim](lowercase, letters/digits/hyphens)[/]",
                "id",
                validator: input =>
                {
                    if (string.IsNullOrWhiteSpace(input))
                        return ValidationResult.Error("Agent ID is required.");
                    if (!System.Text.RegularExpressions.Regex.IsMatch(input, @"^[a-z0-9][a-z0-9\-_]*$"))
                        return ValidationResult.Error("Agent ID must start with a lowercase letter/digit and contain only a-z, 0-9, hyphens, or underscores.");
                    if (existingIds.Contains(input.ToLowerInvariant()))
                        return ValidationResult.Error($"Agent '{input}' already exists.");
                    return ValidationResult.Success();
                })
            .AskText("DisplayName", "Display name", "displayName")
            .AskText("Description", "Description [dim](optional, press Enter to skip)[/]", "description", defaultValue: "")
            .AskSelection("Provider", "Select provider", "provider", providerChoices)
            .AskText("Model", "Model ID", "model", defaultValue: "gpt-4.1")
            .AskConfirm("Enabled", "Enable agent?", "enabled", defaultValue: true)
            .Build();

        var result = await wizard.RunAsync(cancellationToken: cancellationToken);
        if (result.Outcome != WizardOutcome.Completed)
        {
            AnsiConsole.MarkupLine("[yellow]Wizard cancelled.[/]");
            return 1;
        }

        var ctx = result.Context;
        var id = ctx.Get<string>("id");
        var displayName = ctx.Get<string>("displayName");
        var description = ctx.Get<string>("description");
        var provider = ctx.Get<string>("provider");
        var model = ctx.Get<string>("model");
        var enabled = ctx.Get<bool>("enabled");

        if (string.IsNullOrWhiteSpace(displayName))
            displayName = id;

        config.Agents ??= new Dictionary<string, AgentDefinitionConfig>(StringComparer.OrdinalIgnoreCase);
        config.Agents[id] = new AgentDefinitionConfig
        {
            DisplayName = displayName,
            Description = string.IsNullOrWhiteSpace(description) ? null : description,
            Provider = provider,
            Model = model,
            Enabled = enabled,
            Heartbeat = new HeartbeatAgentConfig
            {
                Enabled = true,
                IntervalMinutes = 30,
                QuietHours = new QuietHoursConfig { Enabled = true, Start = "23:00", End = "07:00" }
            }
        };

        var saveCode = await SaveAndValidateAsync(config, configPath, verbose, cancellationToken);
        if (saveCode != 0)
            return saveCode;

        var homePath = Path.GetDirectoryName(configPath) ?? BotNexusHome.ResolveHomePath();
        var botNexusHome = new BotNexusHome(homePath);
        botNexusHome.GetAgentDirectory(id);

        AnsiConsole.MarkupLine($"[green]✓[/] Agent [green]{Markup.Escape(id)}[/] added successfully.");
        return 0;
    }

    public async Task<int> ExecuteAddAsync(string id, string provider, string model, bool enabled, bool verbose, CancellationToken cancellationToken)
        => await ExecuteAddAsync(id, provider, model, enabled, null, null, null, PlatformConfigLoader.DefaultConfigPath, verbose, cancellationToken);

    public async Task<int> ExecuteAddAsync(string id, string provider, string model, bool enabled, string configPath, bool verbose, CancellationToken cancellationToken)
        => await ExecuteAddAsync(id, provider, model, enabled, null, null, null, configPath, verbose, cancellationToken);

    public async Task<int> ExecuteAddAsync(string id, string provider, string model, bool enabled, string? displayName, string? description, string? emoji, string configPath, bool verbose, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Agent ID is required.");
            return 1;
        }

        var config = await LoadConfigRequiredAsync(configPath, cancellationToken);
        if (config is null)
            return 1;

        config.Agents ??= new Dictionary<string, AgentDefinitionConfig>(StringComparer.OrdinalIgnoreCase);
        if (ContainsDictionaryKey(config.Agents, id))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Agent [green]{Markup.Escape(id)}[/] already exists.");
            return 1;
        }

        config.Agents[id] = new AgentDefinitionConfig
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            Description = string.IsNullOrWhiteSpace(description) ? null : description,
            Emoji = string.IsNullOrWhiteSpace(emoji) ? null : emoji,
            Provider = provider,
            Model = model,
            Enabled = enabled,
            Heartbeat = new HeartbeatAgentConfig
            {
                Enabled = true,
                IntervalMinutes = 30,
                QuietHours = new QuietHoursConfig { Enabled = true, Start = "23:00", End = "07:00" }
            }
        };

        var saveCode = await SaveAndValidateAsync(config, configPath, verbose, cancellationToken);
        if (saveCode != 0)
            return saveCode;

        var homePath = Path.GetDirectoryName(configPath) ?? BotNexusHome.ResolveHomePath();
        var botNexusHome = new BotNexusHome(homePath);
        botNexusHome.GetAgentDirectory(id);

        AnsiConsole.MarkupLine($"[green]\u2713[/] Added agent [green]{Markup.Escape(id)}[/].");
        return 0;
    }

    public async Task<int> ExecuteRemoveAsync(string id, bool verbose, CancellationToken cancellationToken)
        => await ExecuteRemoveAsync(id, PlatformConfigLoader.DefaultConfigPath, verbose, cancellationToken);

    public async Task<int> ExecuteRemoveAsync(string id, string configPath, bool verbose, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Agent ID is required.");
            return 1;
        }

        var config = await LoadConfigRequiredAsync(configPath, cancellationToken);
        if (config is null)
            return 1;

        if (config.Agents is null || !TryFindDictionaryKey(config.Agents, id, out var matchedId))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Agent [green]{Markup.Escape(id)}[/] was not found.");
            return 1;
        }

        var defaultAgent = config.Gateway?.DefaultAgentId;
        if (!string.IsNullOrWhiteSpace(defaultAgent) &&
            string.Equals(defaultAgent, matchedId, StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] Removing default agent [green]{Markup.Escape(matchedId)}[/]. Update gateway.defaultAgentId if needed.");
        }

        config.Agents.Remove(matchedId);
        var saveCode = await SaveAndValidateAsync(config, configPath, verbose, cancellationToken);
        if (saveCode != 0)
            return saveCode;

        AnsiConsole.MarkupLine($"[green]\u2713[/] Removed agent [green]{Markup.Escape(matchedId)}[/].");
        return 0;
    }

    public async Task<int> ExecuteShowAsync(string id, string configPath, bool asJson, bool verbose, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Agent ID is required.");
            return 1;
        }

        var config = await LoadConfigRequiredAsync(configPath, cancellationToken);
        if (config is null)
            return 1;

        if (config.Agents is null || !TryFindDictionaryKey(config.Agents, id, out var matchedId))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Agent [green]{Markup.Escape(id)}[/] was not found.");
            return 1;
        }

        var agent = config.Agents[matchedId];

        if (asJson)
        {
            var opts = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
            AnsiConsole.WriteLine(System.Text.Json.JsonSerializer.Serialize(agent, opts));
            return 0;
        }

        var table = new Table().AddColumn("Field").AddColumn("Value");
        table.AddRow("id", Markup.Escape(matchedId));
        table.AddRow("displayName", Markup.Escape(agent.DisplayName ?? string.Empty));
        table.AddRow("description", Markup.Escape(agent.Description ?? string.Empty));
        table.AddRow("provider", Markup.Escape(agent.Provider ?? string.Empty));
        table.AddRow("model", Markup.Escape(agent.Model ?? string.Empty));
        table.AddRow("enabled", agent.Enabled ? "[green]Yes[/]" : "[red]No[/]");
        if (agent.AllowedModels is { Count: > 0 })
            table.AddRow("allowedModels", Markup.Escape(string.Join(", ", agent.AllowedModels)));
        if (agent.SubAgents is { Count: > 0 })
            table.AddRow("subAgents", Markup.Escape(string.Join(", ", agent.SubAgents)));
        if (agent.Extensions is { Count: > 0 })
            table.AddRow("extensions", Markup.Escape(string.Join(", ", agent.Extensions.Keys)));
        AnsiConsole.Write(table);

        if (verbose)
            AnsiConsole.MarkupLine($"[dim]Loaded from: {Markup.Escape(configPath)}[/]");

        return 0;
    }

    private static async Task<PlatformConfig?> LoadConfigRequiredAsync(CancellationToken cancellationToken)
        => await LoadConfigRequiredAsync(PlatformConfigLoader.DefaultConfigPath, cancellationToken);

    private static async Task<PlatformConfig?> LoadConfigRequiredAsync(string configPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Config file not found at [dim]{Markup.Escape(configPath)}[/]. Run [green]botnexus init[/] first.");
            return null;
        }

        try
        {
            return await PlatformConfigLoader.LoadAsync(configPath, cancellationToken, validateOnLoad: false);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Unable to load config: {Markup.Escape(ex.Message)}");
            return null;
        }
    }

    private static async Task<int> SaveAndValidateAsync(PlatformConfig config, bool verbose, CancellationToken cancellationToken)
        => await SaveAndValidateAsync(config, PlatformConfigLoader.DefaultConfigPath, verbose, cancellationToken);

    private static async Task<int> SaveAndValidateAsync(PlatformConfig config, string configPath, bool verbose, CancellationToken cancellationToken)
    {
        await WriteConfigAsync(config, configPath, cancellationToken);

        var reloaded = await PlatformConfigLoader.LoadAsync(configPath, cancellationToken, validateOnLoad: false);
        var errors = PlatformConfigLoader.Validate(reloaded);
        if (errors.Count > 0)
        {
            AnsiConsole.MarkupLine("[red]Config validation failed after write:[/]");
            foreach (var error in errors)
                AnsiConsole.MarkupLine($"  [red]\u2022[/] {Markup.Escape(error)}");
            return 1;
        }

        if (verbose)
            AnsiConsole.MarkupLine($"[dim]Saved config: {Markup.Escape(configPath)}[/]");

        return 0;
    }

    private static async Task WriteConfigAsync(PlatformConfig config, string configPath, CancellationToken cancellationToken, string reason = "before-config-write")
    {
        PlatformConfigLoader.EnsureConfigDirectory(Path.GetDirectoryName(configPath) ?? PlatformConfigLoader.DefaultHomePath);
        var fileSystem = new System.IO.Abstractions.FileSystem();
        var backupsDir = Path.Combine(Path.GetDirectoryName(configPath) ?? BotNexusHome.ResolveHomePath(), "backups");
        var writer = new PlatformConfigWriter(configPath, fileSystem, new ConfigBackupService(backupsDir, fileSystem));
        await writer.UpdatePlatformConfigAsync(config, reason, cancellationToken);
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

}
