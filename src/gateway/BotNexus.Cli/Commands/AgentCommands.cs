using System.CommandLine;
using BotNexus.Cli.Wizard;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using Spectre.Console;
using ValidationResult = Spectre.Console.ValidationResult;
using BotNexus.Cli.Services;

namespace BotNexus.Cli.Commands;

internal sealed class AgentCommands
{
    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var command = new Command("agent", "Manage configured agents.");
        command.AddAlias("agents");

        var listCommand = new Command("list", "List configured agents.");
        listCommand.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
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

        var addCommand = new Command("add", "Add an agent to config.json.")
        {
            idArgument,
            providerOption,
            modelOption,
            enabledOption,
            displayNameOption,
            descriptionOption,
            emojiOption,
            disabledFlag
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
            var target = context.ParseResult.GetValueForOption(targetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            // --disabled flag takes precedence over --enabled
            if (disabled) enabled = false;
            context.ExitCode = await ExecuteAddAsync(id, provider, model, enabled, displayName, description, emoji, configPath, verbose, CancellationToken.None);
        });

        var removeCommand = new Command("remove", "Remove an agent from config.json.")
        {
            idArgument
        };
        removeCommand.SetHandler(async context =>
        {
            var id = context.ParseResult.GetValueForArgument(idArgument);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteRemoveAsync(id, configPath, verbose, CancellationToken.None);
        });

        var wizardCommand = new Command("wizard", "Interactively create a new agent using a step-by-step wizard.");
        wizardCommand.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteWizardAsync(configPath, verbose, CancellationToken.None);
        });

        var showIdArgument = new Argument<string>("id", "Agent ID.");
        var showJsonOption = new Option<bool>("--json", "Emit raw JSON instead of a formatted table.");
        var showCommand = new Command("show", "Show the resolved configuration for a single agent.")
        {
            showIdArgument,
            showJsonOption
        };
        showCommand.SetHandler(async context =>
        {
            var id = context.ParseResult.GetValueForArgument(showIdArgument);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var asJson = context.ParseResult.GetValueForOption(showJsonOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteShowAsync(id, configPath, asJson, verbose, CancellationToken.None);
        });

        var exportIdArgument = new Argument<string>("id", "Agent ID.");
        var exportOutputOption = new Option<string?>("--output", () => null, "Output file path (defaults to <id>.agent.json in the current directory).");
        var exportCommand = new Command("export", "Export an agent as a redacted agentTemplate/v1 JSON template (safe to share; contains no secrets).")
        {
            exportIdArgument,
            exportOutputOption
        };
        exportCommand.SetHandler(async context =>
        {
            var id = context.ParseResult.GetValueForArgument(exportIdArgument);
            var output = context.ParseResult.GetValueForOption(exportOutputOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteExportAsync(id, configPath, output, verbose, CancellationToken.None);
        });

        var importFileArgument = new Argument<string>("file", "Path to an agentTemplate/v1 JSON file to import.");
        var importIdOption = new Option<string?>("--id", () => null, "Target agent ID (defaults to the --set id override, then the template file name).");
        var importSetOption = new Option<string[]>("--set", () => [], "Override a descriptor field before materializing (key=value). Repeatable. Keys: id, displayName, description, emoji, model, provider, thinking, contextWindow.")
        {
            AllowMultipleArgumentsPerToken = false
        };
        var importOverwriteOption = new Option<bool>("--overwrite", () => false, "Replace an existing agent with the same ID. Without this flag an ID collision is refused.");
        var importCommand = new Command("import", "Import an agent from a redacted agentTemplate/v1 JSON template, with optional --set overrides.")
        {
            importFileArgument,
            importIdOption,
            importSetOption,
            importOverwriteOption
        };
        importCommand.SetHandler(async context =>
        {
            var file = context.ParseResult.GetValueForArgument(importFileArgument);
            var idOverride = context.ParseResult.GetValueForOption(importIdOption);
            var sets = context.ParseResult.GetValueForOption(importSetOption) ?? [];
            var overwrite = context.ParseResult.GetValueForOption(importOverwriteOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteImportAsync(file, configPath, idOverride, sets, overwrite, verbose, CancellationToken.None);
        });

        command.AddCommand(listCommand);
        command.AddCommand(addCommand);
        command.AddCommand(removeCommand);
        command.AddCommand(wizardCommand);
        command.AddCommand(showCommand);
        command.AddCommand(exportCommand);
        command.AddCommand(importCommand);
        // #2396: the one subcommand in this group that makes the agent RUN, rather than editing its
        // configuration. It lives in its own file because its concerns (gateway transport, exit
        // codes, stdout/stderr discipline) share nothing with the config-editing subcommands.
        command.AddCommand(AgentExecCommand.Build(verboseOption));
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
                CliText.SafeDisplay(agentId),
                agent.Provider ?? "[dim](unset)[/]",
                agent.Model ?? "[dim](unset)[/]",
                agent.Enabled ? "[green]Yes[/]" : "[red]No[/]");
        }

        AnsiConsole.Write(table);

        if (verbose)
            AnsiConsole.MarkupLine($"[dim]Loaded from: {CliText.SafeDisplay(configPath)}[/]");

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

        var definition = new AgentDefinitionConfig
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

        var saveCode = await SetAgentEntryAsync(configPath, id, definition, verbose, cancellationToken);
        if (saveCode != 0)
            return saveCode;

        var homePath = Path.GetDirectoryName(configPath) ?? BotNexusHome.ResolveHomePath();
        var botNexusHome = new BotNexusHome(homePath);
        botNexusHome.GetAgentDirectory(id);

        AnsiConsole.MarkupLine($"[green]✓[/] Agent [green]{CliText.SafeDisplay(id)}[/] added successfully.");
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
            AnsiConsole.MarkupLine($"[red]Error:[/] Agent [green]{CliText.SafeDisplay(id)}[/] already exists.");
            return 1;
        }

        var definition = new AgentDefinitionConfig
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

        var saveCode = await SetAgentEntryAsync(configPath, id, definition, verbose, cancellationToken);
        if (saveCode != 0)
            return saveCode;

        var homePath = Path.GetDirectoryName(configPath) ?? BotNexusHome.ResolveHomePath();
        var botNexusHome = new BotNexusHome(homePath);
        botNexusHome.GetAgentDirectory(id);

        AnsiConsole.MarkupLine($"[green]\u2713[/] Added agent [green]{CliText.SafeDisplay(id)}[/].");
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
            AnsiConsole.MarkupLine(AgentNotFoundMessage(id));
            return 1;
        }

        var defaultAgent = config.Gateway?.DefaultAgentId;
        if (!string.IsNullOrWhiteSpace(defaultAgent) &&
            string.Equals(defaultAgent, matchedId, StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] Removing default agent [green]{CliText.SafeDisplay(matchedId)}[/]. Update gateway.defaultAgentId if needed.");
        }

        var saveCode = await CliConfigMutation.ApplyAsync(
            configPath,
            document => document.TryRemoveEntry(AgentsPath, matchedId, out var error) ? null : error,
            "before-agent-remove",
            verbose,
            cancellationToken,
            // #2816: removing the last agent legitimately empties `agents`; naming it here keeps
            // that working without granting this command any reach into other sections.
            namedSections: [AgentsPath]);
        if (saveCode != 0)
            return saveCode;

        AnsiConsole.MarkupLine($"[green]\u2713[/] Removed agent [green]{CliText.SafeDisplay(matchedId)}[/].");
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
            AnsiConsole.MarkupLine(AgentNotFoundMessage(id));
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
        foreach (var (field, value) in BuildShowRows(matchedId, agent))
            table.AddRow(field, value);
        AnsiConsole.Write(table);

        if (verbose)
            AnsiConsole.MarkupLine($"[dim]Loaded from: {CliText.SafeDisplay(configPath)}[/]");

        return 0;
    }

    /// <summary>
    /// Builds the field/value rows for <c>agent show</c>. Every value originates in the
    /// on-disk config, which agents can write, so it is rendered through
    /// <see cref="CliText.SafeDisplay"/> (issue #2722). Extracted so the sanitisation is
    /// directly testable without a config file or a console.
    /// </summary>
    internal static List<(string Field, string Value)> BuildShowRows(string matchedId, AgentDefinitionConfig agent)
    {
        var rows = new List<(string, string)>
        {
            ("id", CliText.SafeDisplay(matchedId)),
            ("displayName", CliText.SafeDisplay(agent.DisplayName)),
            ("description", CliText.SafeDisplay(agent.Description)),
            ("provider", CliText.SafeDisplay(agent.Provider)),
            ("model", CliText.SafeDisplay(agent.Model)),
            ("enabled", agent.Enabled ? "[green]Yes[/]" : "[red]No[/]")
        };
        if (agent.AllowedModels is { Count: > 0 })
            rows.Add(("allowedModels", CliText.SafeDisplay(string.Join(", ", agent.AllowedModels))));
        if (agent.SubAgents is { Count: > 0 })
            rows.Add(("subAgents", CliText.SafeDisplay(string.Join(", ", agent.SubAgents))));
        if (agent.Extensions is { Count: > 0 })
            rows.Add(("extensions", CliText.SafeDisplay(string.Join(", ", agent.Extensions.Keys))));
        return rows;
    }

    /// <summary>
    /// The lookup-miss message. It echoes caller-supplied input straight back to the
    /// terminal, so the id is sanitised here rather than at each of the three call sites
    /// (issue #2722).
    /// </summary>
    internal static string AgentNotFoundMessage(string id)
        => $"[red]Error:[/] Agent [green]{CliText.SafeDisplay(id)}[/] was not found.";

    public async Task<int> ExecuteExportAsync(string id, string configPath, string? outputPath, bool verbose, CancellationToken cancellationToken)
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
            AnsiConsole.MarkupLine(AgentNotFoundMessage(id));
            return 1;
        }

        var agent = config.Agents[matchedId];
        var provider = agent.Provider ?? string.Empty;

        var template = new AgentTemplate
        {
            Agent = new AgentTemplateDescriptor
            {
                DisplayName = agent.DisplayName,
                Description = agent.Description,
                Emoji = agent.Emoji,
                ModelId = agent.Model,
                ApiProvider = provider,
                SystemPrompt = ResolveSystemPrompt(agent, configPath),
                ToolIds = agent.ToolIds is { Count: > 0 } ? new List<string>(agent.ToolIds) : null,
                Thinking = agent.Thinking,
                ContextWindow = agent.ContextWindow
            },
            RequiredSecrets = BuildRequiredSecrets(provider)
        };

        var json = template.ToJson();

        var destination = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(Directory.GetCurrentDirectory(), $"{matchedId}.agent.json")
            : outputPath;

        var destinationDir = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(destinationDir))
            Directory.CreateDirectory(destinationDir);

        await File.WriteAllTextAsync(destination, json, cancellationToken);

        AnsiConsole.MarkupLine($"[green]\u2713[/] Exported agent [green]{CliText.SafeDisplay(matchedId)}[/] to [dim]{CliText.SafeDisplay(destination)}[/].");
        if (verbose)
            AnsiConsole.MarkupLine($"[dim]Schema: {AgentTemplate.CurrentSchema}; requiredSecrets: {template.RequiredSecrets.Count}[/]");

        return 0;
    }

    private static string? ResolveSystemPrompt(AgentDefinitionConfig agent, string configPath)
    {
        if (string.IsNullOrWhiteSpace(agent.SystemPromptFile))
            return null;

        var homeDir = Path.GetDirectoryName(configPath) ?? BotNexusHome.ResolveHomePath();
        var promptPath = Path.IsPathRooted(agent.SystemPromptFile)
            ? agent.SystemPromptFile
            : Path.Combine(homeDir, agent.SystemPromptFile);

        return File.Exists(promptPath) ? File.ReadAllText(promptPath) : null;
    }

    private static List<RequiredSecret> BuildRequiredSecrets(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return [];

        return
        [
            new RequiredSecret
            {
                Provider = provider,
                Key = "apiKey",
                Description = $"API key / credential for provider '{provider}'. Supply via providers.{provider}.apiKey or auth.json."
            }
        ];
    }

    /// <summary>
    /// Import an agent from a redacted <c>agentTemplate/v1</c> template, applying any
    /// <c>--set key=value</c> overrides before the agent is materialized into config.json.
    /// This is the symmetric inverse of <see cref="ExecuteExportAsync"/>: it reconstructs
    /// exactly the descriptor fields export bundles (identity, model/provider, system
    /// prompt, tools, thinking, context window) and never silently reuses the exporter's id
    /// or overwrites an existing agent unless <paramref name="overwrite"/> is set.
    /// </summary>
    /// <param name="filePath">Path to the template JSON file.</param>
    /// <param name="configPath">Target config.json to write the reconstructed agent into.</param>
    /// <param name="idOverride">Explicit target agent id from <c>--id</c>; falls back to the
    /// <c>id</c> <c>--set</c> override, then the template file name.</param>
    /// <param name="sets">Repeatable <c>key=value</c> overrides that supersede template values.</param>
    /// <param name="overwrite">When true, replace an existing agent with the resolved id.</param>
    public async Task<int> ExecuteImportAsync(
        string filePath,
        string configPath,
        string? idOverride,
        IReadOnlyList<string> sets,
        bool overwrite,
        bool verbose,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Template file path is required.");
            return 1;
        }

        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Template file not found at [dim]{CliText.SafeDisplay(filePath)}[/].");
            return 1;
        }

        AgentTemplate? template;
        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken);
            template = AgentTemplate.FromJson(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Template is not valid JSON: {CliText.SafeDisplay(ex.Message)}");
            return 1;
        }

        if (template is null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Template could not be parsed.");
            return 1;
        }

        // Parse and apply --set overrides onto the descriptor before validation so a
        // template missing a field can still be completed at import time.
        if (!TryParseSets(sets, out var overrides, out var setError))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {CliText.SafeDisplay(setError)}");
            return 1;
        }

        if (!TryApplyOverrides(template.Agent, overrides, out var applyError))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {CliText.SafeDisplay(applyError)}");
            return 1;
        }

        var schemaErrors = template.Validate();
        if (schemaErrors.Count > 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Template failed schema validation:");
            foreach (var error in schemaErrors)
                AnsiConsole.MarkupLine($"  [red]\u2022[/] {CliText.SafeDisplay(error)}");
            return 1;
        }

        // Resolve the target id: --id, then --set id, then the template file name stem.
        var targetId = idOverride;
        if (string.IsNullOrWhiteSpace(targetId) && overrides.TryGetValue("id", out var idFromSet))
            targetId = idFromSet;
        if (string.IsNullOrWhiteSpace(targetId))
            targetId = DeriveIdFromFileName(filePath);

        if (string.IsNullOrWhiteSpace(targetId))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Could not resolve a target agent id. Pass --id or --set id=<value>.");
            return 1;
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(targetId, @"^[a-z0-9][a-z0-9\-_]*$"))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Invalid agent id [green]{CliText.SafeDisplay(targetId)}[/]. Use lowercase letters, digits, hyphens, or underscores.");
            return 1;
        }

        var config = await LoadConfigRequiredAsync(configPath, cancellationToken);
        if (config is null)
            return 1;

        config.Agents ??= new Dictionary<string, AgentDefinitionConfig>(StringComparer.OrdinalIgnoreCase);

        var exists = TryFindDictionaryKey(config.Agents, targetId, out var existingKey);
        if (exists && !overwrite)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Agent [green]{CliText.SafeDisplay(targetId)}[/] already exists. Pass [green]--overwrite[/] to replace it, or choose another id with [green]--id[/] / [green]--set id=<value>[/].");
            return 1;
        }

        // Materialize the reconstructed agent definition from the descriptor.
        var descriptor = template.Agent;
        var agent = new AgentDefinitionConfig
        {
            DisplayName = string.IsNullOrWhiteSpace(descriptor.DisplayName) ? null : descriptor.DisplayName,
            Description = string.IsNullOrWhiteSpace(descriptor.Description) ? null : descriptor.Description,
            Emoji = string.IsNullOrWhiteSpace(descriptor.Emoji) ? null : descriptor.Emoji,
            Provider = descriptor.ApiProvider,
            Model = descriptor.ModelId,
            ToolIds = descriptor.ToolIds is { Count: > 0 } ? new List<string>(descriptor.ToolIds) : null,
            Thinking = descriptor.Thinking,
            ContextWindow = descriptor.ContextWindow,
            Enabled = true,
            Heartbeat = new HeartbeatAgentConfig
            {
                Enabled = true,
                IntervalMinutes = 30,
                QuietHours = new QuietHoursConfig { Enabled = true, Start = "23:00", End = "07:00" }
            }
        };

        var homeDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? BotNexusHome.ResolveHomePath();

        // Restore the system prompt into the agent workspace and reference it by relative
        // path so the reconstructed agent is self-contained and portable.
        if (!string.IsNullOrWhiteSpace(descriptor.SystemPrompt))
        {
            var botNexusHome = new BotNexusHome(homeDir);
            var agentDir = botNexusHome.GetAgentDirectory(targetId);
            var promptPath = Path.Combine(agentDir, "IMPORTED_SYSTEM_PROMPT.md");
            await File.WriteAllTextAsync(promptPath, descriptor.SystemPrompt, cancellationToken);
            agent.SystemPromptFile = Path.GetRelativePath(homeDir, promptPath).Replace('\\', '/');
        }

        var saveCode = await CliConfigMutation.ApplyAsync(
            configPath,
            document =>
            {
                // Import replaces the whole agent entry by design (it reconstructs a complete
                // descriptor), so remove any differently-cased existing key first rather than
                // leaving a duplicate sibling behind.
                if (exists && !document.TryRemoveEntry(AgentsPath, existingKey, out var removeError))
                    return removeError;

                return document.TrySetEntryFrom(AgentsPath, targetId, agent, out var setError)
                    ? null
                    : setError;
            },
            "before-agent-import",
            verbose,
            cancellationToken,
            namedSections: [AgentsPath]);
        if (saveCode != 0)
            return saveCode;

        // Ensure the workspace directory exists even when there is no system prompt to write.
        new BotNexusHome(homeDir).GetAgentDirectory(targetId);

        var verb = exists ? "Replaced" : "Imported";
        AnsiConsole.MarkupLine($"[green]\u2713[/] {verb} agent [green]{CliText.SafeDisplay(targetId)}[/] from template [dim]{CliText.SafeDisplay(Path.GetFileName(filePath))}[/].");
        if (template.RequiredSecrets is { Count: > 0 })
        {
            AnsiConsole.MarkupLine("[yellow]Required secrets to re-provide before this agent can run:[/]");
            foreach (var secret in template.RequiredSecrets)
                AnsiConsole.MarkupLine($"  [yellow]\u2022[/] {CliText.SafeDisplay(secret.Provider)}.{CliText.SafeDisplay(secret.Key)}");
        }
        if (verbose)
            AnsiConsole.MarkupLine($"[dim]Schema: {AgentTemplate.CurrentSchema}; overrides applied: {overrides.Count}[/]");

        return 0;
    }

    private static string DeriveIdFromFileName(string filePath)
    {
        var name = Path.GetFileName(filePath);
        // Strip the conventional ".agent.json" (and a bare ".json") suffix.
        foreach (var suffix in new[] { ".agent.json", ".json" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }
        return name.ToLowerInvariant();
    }

    private static bool TryParseSets(IReadOnlyList<string> sets, out Dictionary<string, string> overrides, out string error)
    {
        overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;
        foreach (var raw in sets)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var idx = raw.IndexOf('=');
            if (idx <= 0)
            {
                error = $"Invalid --set '{raw}'. Expected key=value.";
                return false;
            }
            var key = raw[..idx].Trim();
            var value = raw[(idx + 1)..];
            overrides[key] = value;
        }
        return true;
    }

    // Applies parsed overrides to the descriptor. 'id' is handled by the caller for target
    // resolution, not on the descriptor itself.
    private static bool TryApplyOverrides(AgentTemplateDescriptor descriptor, Dictionary<string, string> overrides, out string error)
    {
        error = string.Empty;
        foreach (var (key, value) in overrides)
        {
            switch (key.ToLowerInvariant())
            {
                case "id":
                    break; // resolved by caller
                case "displayname":
                    descriptor.DisplayName = value;
                    break;
                case "description":
                    descriptor.Description = value;
                    break;
                case "emoji":
                    descriptor.Emoji = value;
                    break;
                case "model":
                case "modelid":
                    descriptor.ModelId = value;
                    break;
                case "provider":
                case "apiprovider":
                    descriptor.ApiProvider = value;
                    break;
                case "systemprompt":
                    descriptor.SystemPrompt = value;
                    break;
                case "contextwindow":
                    if (!int.TryParse(value, out var ctx))
                    {
                        error = $"--set contextWindow='{value}' is not an integer.";
                        return false;
                    }
                    descriptor.ContextWindow = ctx;
                    break;
                case "thinking":
                    if (!Enum.TryParse<BotNexus.Agent.Providers.Core.Models.ThinkingLevel>(value, ignoreCase: true, out var thinking))
                    {
                        error = $"--set thinking='{value}' is not a valid thinking level.";
                        return false;
                    }
                    descriptor.Thinking = thinking switch
                    {
                        BotNexus.Agent.Providers.Core.Models.ThinkingLevel.ExtraHigh => "xhigh",
                        _ => value.ToLowerInvariant()
                    };
                    break;
                default:
                    error = $"Unknown --set key '{key}'. Supported: id, displayName, description, emoji, model, provider, systemPrompt, thinking, contextWindow.";
                    return false;
            }
        }
        return true;
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

    /// <summary>Canonical path of the agents section.</summary>
    private const string AgentsPath = "agents";

    private static async Task<int> SetAgentEntryAsync(
        string configPath,
        string agentId,
        AgentDefinitionConfig agent,
        bool verbose,
        CancellationToken cancellationToken)
        => await CliConfigMutation.ApplyAsync(
            configPath,
            document => document.TrySetEntryFrom(AgentsPath, agentId, agent, out var error) ? null : error,
            "before-agent-update",
            verbose,
            cancellationToken,
            namedSections: [AgentsPath]);

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

