using System.CommandLine;
using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

internal sealed class SatelliteCommand
{
    private static readonly string[] ValidPlatforms = ["windows", "macos", "linux"];
    private static readonly string[] ValidCapabilities = ["notify", "canvas", "exec"];

    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var command = new Command("satellite", "Manage satellite nodes.");
        command.AddAlias("satellites");

        // --- list ---
        var listCommand = new Command("list", "List all registered satellites.");
        listCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteListAsync(configPath, CancellationToken.None);
        });

        // --- register ---
        var nameArgument = new Argument<string>("name", "Satellite ID (e.g., sat_desktop_home).");
        var displayNameOption = new Option<string?>("--display-name", "Human-readable display name.");
        var platformOption = new Option<string>("--platform", () => "windows", "Platform: windows, macos, linux.");
        var capabilitiesOption = new Option<string>("--capabilities", () => "notify,canvas", "Comma-separated capabilities: notify, canvas, exec.");
        var ownerOption = new Option<string>("--owner", "Owner user ID.") { IsRequired = true };

        var registerCommand = new Command("register", "Register a new satellite and generate its API key.")
        {
            nameArgument,
            displayNameOption,
            platformOption,
            capabilitiesOption,
            ownerOption
        };
        registerCommand.SetHandler(async context =>
        {
            var name = context.ParseResult.GetValueForArgument(nameArgument);
            var displayName = context.ParseResult.GetValueForOption(displayNameOption);
            var platform = context.ParseResult.GetValueForOption(platformOption) ?? "windows";
            var capabilities = context.ParseResult.GetValueForOption(capabilitiesOption) ?? "notify,canvas";
            var owner = context.ParseResult.GetValueForOption(ownerOption)!;
            var target = context.ParseResult.GetValueForOption(targetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteRegisterAsync(name, displayName, platform, capabilities, owner, configPath, CancellationToken.None);
        });

        // --- remove ---
        var removeCommand = new Command("remove", "Remove a satellite from config.")
        {
            nameArgument
        };
        removeCommand.AddAlias("delete");
        removeCommand.SetHandler(async context =>
        {
            var name = context.ParseResult.GetValueForArgument(nameArgument);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteRemoveAsync(name, configPath, CancellationToken.None);
        });

        command.AddCommand(listCommand);
        command.AddCommand(registerCommand);
        command.AddCommand(removeCommand);
        return command;
    }

    private static async Task<int> ExecuteListAsync(string configPath, CancellationToken ct)
    {
        var fileSystem = new FileSystem();
        if (!fileSystem.File.Exists(configPath))
        {
            AnsiConsole.MarkupLine("[yellow]No config.json found.[/]");
            return 1;
        }

        var json = await fileSystem.File.ReadAllTextAsync(configPath, ct);
        var config = JsonSerializer.Deserialize<PlatformConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var satellites = config?.Gateway?.Satellites;

        if (satellites is null || satellites.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No satellites registered.[/]");
            return 0;
        }

        var table = new Table()
            .AddColumn("ID")
            .AddColumn("Display Name")
            .AddColumn("Platform")
            .AddColumn("Owner")
            .AddColumn("Capabilities")
            .AddColumn("Enabled");

        foreach (var (id, sat) in satellites.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            table.AddRow(
                Markup.Escape(id),
                Markup.Escape(sat.DisplayName ?? "(none)"),
                Markup.Escape(sat.Platform),
                Markup.Escape(sat.OwnerUserId ?? "(none)"),
                Markup.Escape(string.Join(", ", sat.Capabilities ?? [])),
                sat.Enabled ? "[green]yes[/]" : "[red]no[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n{satellites.Count} satellite(s) registered.");
        return 0;
    }

    private static async Task<int> ExecuteRegisterAsync(
        string name, string? displayName, string platform, string capabilities,
        string owner, string configPath, CancellationToken ct)
    {
        // Validate platform
        if (!ValidPlatforms.Contains(platform.ToLowerInvariant()))
        {
            AnsiConsole.MarkupLine($"[red]Invalid platform: {Markup.Escape(platform)}. Must be one of: {string.Join(", ", ValidPlatforms)}[/]");
            return 1;
        }

        // Validate capabilities
        var capList = capabilities.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToLowerInvariant())
            .ToList();
        var invalidCaps = capList.Except(ValidCapabilities).ToList();
        if (invalidCaps.Count > 0)
        {
            AnsiConsole.MarkupLine($"[red]Invalid capabilities: {string.Join(", ", invalidCaps)}. Valid: {string.Join(", ", ValidCapabilities)}[/]");
            return 1;
        }

        // Generate API key
        var apiKey = SatelliteKeyGenerator.GenerateApiKey();

        // Build config entry
        var satConfig = new SatelliteConfig
        {
            DisplayName = displayName ?? name,
            Platform = platform.ToLowerInvariant(),
            ApiKey = apiKey,
            Capabilities = capList,
            OwnerUserId = owner,
            Enabled = true
        };

        // Write to config
        var fileSystem = new FileSystem();
        var writer = new PlatformConfigWriter(configPath, fileSystem);

        await writer.MutateAsync(root =>
        {
            if (root["gateway"] is not JsonObject gateway)
            {
                gateway = new JsonObject();
                root["gateway"] = gateway;
            }

            if (gateway["satellites"] is not JsonObject satellites)
            {
                satellites = new JsonObject();
                gateway["satellites"] = satellites;
            }

            if (satellites.ContainsKey(name))
            {
                throw new InvalidOperationException($"Satellite '{name}' already exists. Use 'satellite remove' first.");
            }

            var serialized = JsonSerializer.SerializeToNode(satConfig, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            satellites[name] = serialized;
        }, "satellite-register", ct);

        // Display success
        AnsiConsole.MarkupLine($"[green]✓[/] Satellite [bold]{Markup.Escape(name)}[/] registered.");
        AnsiConsole.WriteLine();

        var panel = new Panel(
            new Rows(
                new Markup($"[bold]Satellite ID:[/]  {Markup.Escape(name)}"),
                new Markup($"[bold]Display Name:[/] {Markup.Escape(displayName ?? name)}"),
                new Markup($"[bold]Platform:[/]     {Markup.Escape(platform)}"),
                new Markup($"[bold]Owner:[/]        {Markup.Escape(owner)}"),
                new Markup($"[bold]Capabilities:[/] {Markup.Escape(string.Join(", ", capList))}"),
                new Markup(""),
                new Markup($"[bold yellow]API Key:[/]      [bold]{Markup.Escape(apiKey)}[/]")))
        {
            Header = new PanelHeader(" Satellite Registered "),
            Border = BoxBorder.Rounded
        };
        AnsiConsole.Write(panel);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]⚠ Save this API key now — it cannot be recovered.[/]");
        AnsiConsole.MarkupLine("[dim]The satellite will use this key to authenticate with the gateway.[/]");

        return 0;
    }

    private static async Task<int> ExecuteRemoveAsync(string name, string configPath, CancellationToken ct)
    {
        var fileSystem = new FileSystem();
        if (!fileSystem.File.Exists(configPath))
        {
            AnsiConsole.MarkupLine("[red]No config.json found.[/]");
            return 1;
        }

        var removed = false;
        await new PlatformConfigWriter(configPath, fileSystem).MutateAsync(root =>
        {
            if (root["gateway"] is JsonObject gateway &&
                gateway["satellites"] is JsonObject satellites &&
                satellites.ContainsKey(name))
            {
                satellites.Remove(name);
                removed = true;
            }
        }, "satellite-remove", ct);

        if (removed)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Satellite [bold]{Markup.Escape(name)}[/] removed.");
            return 0;
        }

        AnsiConsole.MarkupLine($"[yellow]Satellite '{Markup.Escape(name)}' not found in config.[/]");
        return 1;
    }
}
