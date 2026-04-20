using System.CommandLine;
using System.Text.Json;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

internal sealed class ValidateCommand
{
    public Command Build(Option<bool> verboseOption)
    {
        var remoteOption = new Option<bool>("--remote", "Validate using the running gateway /api/config/validate endpoint.");
        var gatewayUrlOption = new Option<string?>("--gateway-url", "Gateway base URL override for remote validation.");
        var command = new Command("validate", "Validate BotNexus platform configuration.")
        {
            remoteOption,
            gatewayUrlOption
        };

        command.SetHandler(async context =>
        {
            var remote = context.ParseResult.GetValueForOption(remoteOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var gatewayUrlOverride = context.ParseResult.GetValueForOption(gatewayUrlOption);
            context.ExitCode = remote
                ? await ExecuteRemoteAsync(gatewayUrlOverride, verbose, CancellationToken.None)
                : await ExecuteAsync(verbose, CancellationToken.None);
        });

        return command;
    }

    public async Task<int> ExecuteAsync(bool verbose, CancellationToken cancellationToken)
    {
        var configPath = PlatformConfigLoader.DefaultConfigPath;
        AnsiConsole.MarkupLine("BotNexus config validation [dim](local)[/]");
        AnsiConsole.MarkupLine($"Config path: [dim]{Markup.Escape(configPath)}[/]");

        if (!File.Exists(configPath))
        {
            PrintResult(
                valid: false,
                warnings: [],
                errors:
                [
                    $"Config file not found at '{configPath}'.",
                    "Create ~/.botnexus/config.json (or set BOTNEXUS_HOME) and retry."
                ]);
            return 1;
        }

        PlatformConfig config;
        try
        {
            config = await PlatformConfigLoader.LoadAsync(configPath, cancellationToken, validateOnLoad: false);
        }
        catch (Exception ex)
        {
            PrintResult(valid: false, warnings: [], errors: [$"Unable to load config: {ex.Message}"]);
            return 1;
        }

        var errors = PlatformConfigLoader.Validate(config);
        var warnings = PlatformConfigLoader.ValidateWarnings(config);
        if (verbose)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Validation trace:[/]");
            AnsiConsole.MarkupLine($"[dim]- Loaded config file: {Markup.Escape(configPath)}[/]");
            AnsiConsole.MarkupLine($"[dim]- Ran {nameof(PlatformConfigLoader)}.{nameof(PlatformConfigLoader.Validate)}[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Config details:[/]");
            AnsiConsole.WriteLine(JsonSerializer.Serialize(config, CreateWriteJsonOptions()));
        }

        PrintResult(valid: errors.Count == 0, warnings, errors);
        return errors.Count == 0 ? 0 : 1;
    }

    public async Task<int> ExecuteRemoteAsync(string? gatewayUrlOverride, bool verbose, CancellationToken cancellationToken)
    {
        var gatewayUrl = ResolveGatewayUrl(gatewayUrlOverride);
        if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var gatewayBaseUri))
        {
            PrintResult(valid: false, warnings: [], errors: [$"Invalid gateway URL '{gatewayUrl}'."]);
            return 1;
        }

        var endpoint = new Uri(gatewayBaseUri, "/api/config/validate");
        AnsiConsole.MarkupLine("BotNexus config validation [dim](remote)[/]");
        AnsiConsole.MarkupLine($"Gateway URL: [dim]{Markup.Escape(gatewayBaseUri.ToString())}[/]");
        AnsiConsole.MarkupLine($"Endpoint: [dim]{Markup.Escape(endpoint.ToString())}[/]");

        using var httpClient = new HttpClient();
        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync(endpoint, cancellationToken);
        }
        catch (Exception ex)
        {
            PrintResult(valid: false, warnings: [], errors: [$"Remote validation request failed: {ex.Message}"]);
            return 1;
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            PrintResult(valid: false, warnings: [], errors: [$"Gateway returned {(int)response.StatusCode} {response.ReasonPhrase}.", payload]);
            return 1;
        }

        ConfigValidationResponse? validation;
        try
        {
            validation = JsonSerializer.Deserialize<ConfigValidationResponse>(payload, CreateReadJsonOptions());
        }
        catch (Exception ex)
        {
            PrintResult(valid: false, warnings: [], errors: [$"Unable to parse gateway response: {ex.Message}", payload]);
            return 1;
        }

        if (validation is null)
        {
            PrintResult(valid: false, warnings: [], errors: ["Gateway response was empty."]);
            return 1;
        }

        if (verbose)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Validation trace:[/]");
            AnsiConsole.MarkupLine($"[dim]- GET {Markup.Escape(endpoint.ToString())}[/]");
            AnsiConsole.MarkupLine($"[dim]- HTTP {(int)response.StatusCode} {response.ReasonPhrase}[/]");
            AnsiConsole.MarkupLine($"[dim]- Validated config path: {Markup.Escape(validation.ConfigPath)}[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Response details:[/]");
            AnsiConsole.WriteLine(payload);
        }

        PrintResult(validation.IsValid, validation.Warnings ?? [], validation.Errors ?? []);
        return validation.IsValid ? 0 : 1;
    }

    private static string ResolveGatewayUrl(string? gatewayUrlOverride)
    {
        if (!string.IsNullOrWhiteSpace(gatewayUrlOverride))
            return gatewayUrlOverride;

        try
        {
            var config = PlatformConfigLoader.Load(validateOnLoad: false);
            return config.Gateway?.ListenUrl ?? "http://localhost:5005";
        }
        catch
        {
            return "http://localhost:5005";
        }
    }

    private static void PrintResult(bool valid, IReadOnlyList<string> warnings, IReadOnlyList<string> errors)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(valid
            ? "[green]Result: VALID ✓[/]"
            : "[red]Result: INVALID ✗[/]");

        if (warnings.Count > 0)
        {
            AnsiConsole.MarkupLine("\n[yellow]Warnings:[/]");
            foreach (var warning in warnings)
                AnsiConsole.MarkupLine($"  [yellow]•[/] {Markup.Escape(warning)}");
        }

        if (errors.Count > 0)
        {
            AnsiConsole.MarkupLine("\n[red]Errors:[/]");
            foreach (var error in errors)
                AnsiConsole.MarkupLine($"  [red]•[/] {Markup.Escape(error)}");
        }

        if (warnings.Count == 0 && errors.Count == 0)
            AnsiConsole.MarkupLine("[dim]No warnings or errors.[/]");
    }

    private static JsonSerializerOptions CreateWriteJsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static JsonSerializerOptions CreateReadJsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true
    };
}

internal sealed record ConfigValidationResponse(
    bool IsValid,
    string ConfigPath,
    IReadOnlyList<string>? Warnings,
    IReadOnlyList<string>? Errors);
