using System.CommandLine;
using System.Text.Json;
using BotNexus.Gateway.Configuration;

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
        Console.WriteLine("BotNexus config validation (local)");
        Console.WriteLine($"Config path: {configPath}");

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
            Console.WriteLine();
            Console.WriteLine("Validation trace:");
            Console.WriteLine($"- Loaded config file: {configPath}");
            Console.WriteLine($"- Ran {nameof(PlatformConfigLoader)}.{nameof(PlatformConfigLoader.Validate)}");
            Console.WriteLine();
            Console.WriteLine("Config details:");
            Console.WriteLine(JsonSerializer.Serialize(config, CreateWriteJsonOptions()));
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
        Console.WriteLine("BotNexus config validation (remote)");
        Console.WriteLine($"Gateway URL: {gatewayBaseUri}");
        Console.WriteLine($"Endpoint: {endpoint}");

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
            Console.WriteLine();
            Console.WriteLine("Validation trace:");
            Console.WriteLine($"- GET {endpoint}");
            Console.WriteLine($"- HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            Console.WriteLine($"- Validated config path: {validation.ConfigPath}");
            Console.WriteLine();
            Console.WriteLine("Response details:");
            Console.WriteLine(payload);
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
            return config.GetListenUrl() ?? "http://localhost:5005";
        }
        catch
        {
            return "http://localhost:5005";
        }
    }

    private static void PrintResult(bool valid, IReadOnlyList<string> warnings, IReadOnlyList<string> errors)
    {
        Console.WriteLine();
        Console.WriteLine(valid ? "Result: VALID ✅" : "Result: INVALID ❌");

        Console.WriteLine("Warnings:");
        if (warnings.Count == 0)
        {
            Console.WriteLine("- (none)");
        }
        else
        {
            foreach (var warning in warnings)
                Console.WriteLine($"- {warning}");
        }

        Console.WriteLine("Errors:");
        if (errors.Count == 0)
        {
            Console.WriteLine("- (none)");
        }
        else
        {
            foreach (var error in errors)
                Console.WriteLine($"- {error}");
        }
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
