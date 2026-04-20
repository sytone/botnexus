using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

internal sealed class ConfigCommands(IConfigPathResolver configPathResolver)
{
    public Command Build(Option<bool> verboseOption)
    {
        var command = new Command("config", "Read and update BotNexus configuration.");

        var keyArgument = new Argument<string>("key", "Dotted config key path (example: gateway.listenUrl).");
        var getCommand = new Command("get", "Get a config value by dotted key.")
        {
            keyArgument
        };
        getCommand.SetHandler(async context =>
        {
            var key = context.ParseResult.GetValueForArgument(keyArgument);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteGetAsync(key, verbose, CancellationToken.None);
        });

        var valueArgument = new Argument<string>("value", "Value to set.");
        var setCommand = new Command("set", "Set a config value by dotted key.")
        {
            keyArgument,
            valueArgument
        };
        setCommand.SetHandler(async context =>
        {
            var key = context.ParseResult.GetValueForArgument(keyArgument);
            var value = context.ParseResult.GetValueForArgument(valueArgument);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteSetAsync(key, value, verbose, CancellationToken.None);
        });

        var schemaOutputOption = new Option<string>("--output", () => "docs\\botnexus-config.schema.json", "Schema output path.");
        var schemaCommand = new Command("schema", "Generate JSON schema for platform config.")
        {
            schemaOutputOption
        };
        schemaCommand.SetHandler(async context =>
        {
            var outputPath = context.ParseResult.GetValueForOption(schemaOutputOption) ?? "docs\\botnexus-config.schema.json";
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteSchemaAsync(outputPath, verbose, CancellationToken.None);
        });

        command.AddCommand(getCommand);
        command.AddCommand(setCommand);
        command.AddCommand(schemaCommand);
        return command;
    }

    public async Task<int> ExecuteGetAsync(string keyPath, bool verbose, CancellationToken cancellationToken)
    {
        var config = await LoadConfigRequiredAsync(cancellationToken);
        if (config is null)
            return 1;

        if (!configPathResolver.TryGetValue(config, keyPath, out var value, out var error))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(error)}");
            return 1;
        }

        PrintValue(value);
        if (verbose)
            AnsiConsole.MarkupLine($"[dim]Read key: {Markup.Escape(keyPath)}[/]");

        return 0;
    }

    public async Task<int> ExecuteSetAsync(string keyPath, string rawValue, bool verbose, CancellationToken cancellationToken)
    {
        var config = await LoadConfigRequiredAsync(cancellationToken);
        if (config is null)
            return 1;

        if (!configPathResolver.TrySetValue(config, keyPath, rawValue, out var error))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(error)}");
            return 1;
        }

        var saveCode = await SaveAndValidateAsync(config, verbose, cancellationToken);
        if (saveCode != 0)
            return saveCode;

        AnsiConsole.MarkupLine($"[green]\u2713[/] Set [green]{Markup.Escape(keyPath)}[/].");
        return 0;
    }

    public Task<int> ExecuteSchemaAsync(string outputPath, bool verbose, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Output path is required.");
            return Task.FromResult(1);
        }

        var resolvedPath = Path.GetFullPath(outputPath);
        PlatformConfigSchema.WriteSchema(resolvedPath);

        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(1);

        AnsiConsole.MarkupLine($"[green]\u2713[/] Generated schema: [dim]{Markup.Escape(resolvedPath)}[/]");
        if (verbose)
        {
            var availablePaths = configPathResolver.GetAvailablePaths(new PlatformConfig());
            AnsiConsole.MarkupLine($"[dim]Schema generated from model graph ({availablePaths.Count} discoverable config paths).[/]");
        }

        return Task.FromResult(0);
    }

    private static async Task<PlatformConfig?> LoadConfigRequiredAsync(CancellationToken cancellationToken)
    {
        var configPath = PlatformConfigLoader.DefaultConfigPath;
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
    {
        var configPath = PlatformConfigLoader.DefaultConfigPath;
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

    private static async Task WriteConfigAsync(PlatformConfig config, string configPath, CancellationToken cancellationToken)
    {
        PlatformConfigLoader.EnsureConfigDirectory(PlatformConfigLoader.DefaultHomePath);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, CreateWriteJsonOptions()), cancellationToken);
    }

    private static void PrintValue(object? value)
    {
        if (value is null)
        {
            AnsiConsole.WriteLine("null");
            return;
        }

        if (value is string stringValue)
        {
            AnsiConsole.WriteLine(stringValue);
            return;
        }

        AnsiConsole.WriteLine(JsonSerializer.Serialize(value, CreateWriteJsonOptions()));
    }

    private static JsonSerializerOptions CreateWriteJsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
