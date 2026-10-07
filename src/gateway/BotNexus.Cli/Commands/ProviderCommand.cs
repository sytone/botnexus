using System.CommandLine;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BotNexus.Agent.Providers.Copilot;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Cli.Commands.Provider;
using BotNexus.Cli.Diagnostics;
using BotNexus.Cli.Wizard;
using BotNexus.Gateway.Configuration;
using Spectre.Console;
using BotNexus.Cli.Services;

namespace BotNexus.Cli.Commands;

internal sealed class ProviderCommand
{
    private static readonly string[] KnownProviders = ["github-copilot", "openai", "anthropic", "ollama"];

    private static readonly Dictionary<string, string> ProviderDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["github-copilot"] = "GitHub Copilot (OAuth — free with GitHub account)",
        ["openai"] = "OpenAI (API key required)",
        ["anthropic"] = "Anthropic (API key required)",
        ["ollama"] = "Ollama (local — no API key required)"
    };

    private static readonly Dictionary<string, string> ProviderAuthModes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["github-copilot"] = "oauth",
        ["openai"] = "apikey",
        ["anthropic"] = "apikey",
        ["ollama"] = "none"
    };

    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var command = new Command("provider", "Configure and authenticate LLM providers.");

        var setupCommand = new Command("setup", "Interactively add and authenticate a new provider.");
        var setupProviderOption = new Option<string?>("--provider", () => null,
            $"Pre-select the provider to configure ({string.Join(" | ", KnownProviders)}). Skips the interactive provider-selection prompt and runs the rest of the setup flow (API-key prompt or OAuth device-code flow). Useful for scripting and integration tests.");
        setupCommand.Add(setupProviderOption);
        setupCommand.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var preselected = context.ParseResult.GetValueForOption(setupProviderOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteSetupAsync(configPath, home, verbose, preselected, CancellationToken.None);
        });

        var listCommand = new Command("list", "List configured providers.");
        listCommand.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteListAsync(configPath, verbose, CancellationToken.None);
        });

        command.AddCommand(setupCommand);
        command.AddCommand(listCommand);
        command.AddCommand(BuildAddCommand(verboseOption, targetOption));
        command.AddCommand(BuildRemoveCommand(verboseOption, targetOption));
        command.AddCommand(BuildTestCommand());
        command.AddCommand(CopilotProviderSubcommand.Build(
            verboseOption, targetOption,
            (configPath, home, verbose, instance, ct) => ExecuteCopilotSetupAsync(configPath, home, verbose, instance, ct)));
        command.AddCommand(OllamaProviderSubcommand.Build(targetOption));

        // Default to setup when no subcommand given
        var defaultProviderOption = new Option<string?>("--provider", () => null,
            $"Pre-select the provider to configure ({string.Join(" | ", KnownProviders)}). Skips the interactive provider-selection prompt.") { IsHidden = true };
        command.Add(defaultProviderOption);
        command.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var preselected = context.ParseResult.GetValueForOption(defaultProviderOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = preselected is null
                ? await ExecuteDefaultAsync(configPath, home, verbose, CancellationToken.None)
                : await ExecuteSetupAsync(configPath, home, verbose, preselected, CancellationToken.None);
        });

        return command;
    }

    private static Command BuildAddCommand(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var cmd = new Command("add", "Add or update a provider non-interactively. Useful for scripts and CI.");
        var nameOpt = new Option<string>("--name", "Provider name (e.g. 'openai', 'integration-mock').") { IsRequired = true };
        var typeOpt = new Option<string?>("--type", () => null, "Built-in provider type. Use 'microsoft-foundry' for Microsoft Foundry Responses instances.");
        var apiOpt = new Option<string?>("--api", () => null, "API contract handled by this provider (e.g. 'openai-completions', 'openai-responses', 'anthropic-messages', 'integration-mock'). Defaults to 'openai-completions'.");
        var apiKeyOpt = new Option<string?>("--api-key", () => null, "API key value, or 'auth:<name>' to reference an auth.json OAuth entry.");
        var authenticationOpt = new Option<string?>("--authentication", () => null, "Authentication mode for Microsoft Foundry: entra-default, managed-identity, user-assigned-managed-identity, or api-key.");
        var clientIdOpt = new Option<string?>("--client-id", () => null, "Client ID for user-assigned managed identity authentication.");
        var baseUrlOpt = new Option<string?>("--base-url", () => null, "Base URL for OpenAI-compatible endpoints, or catalog path for 'integration-mock'.");
        var defaultModelOpt = new Option<string?>("--default-model", () => null, "Default model id for this provider.");
        var modelsOpt = new Option<string[]>("--model", () => Array.Empty<string>(), "Allowed model id (repeatable). Omit to allow all models registered for this provider.")
        {
            AllowMultipleArgumentsPerToken = true
        };
        var disabledOpt = new Option<bool>("--disabled", () => false, "Mark the provider as disabled. Disabled providers are hidden from the API.");

        cmd.AddOption(nameOpt);
        cmd.AddOption(typeOpt);
        cmd.AddOption(apiOpt);
        cmd.AddOption(apiKeyOpt);
        cmd.AddOption(authenticationOpt);
        cmd.AddOption(clientIdOpt);
        cmd.AddOption(baseUrlOpt);
        cmd.AddOption(defaultModelOpt);
        cmd.AddOption(modelsOpt);
        cmd.AddOption(disabledOpt);

        cmd.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            var name = context.ParseResult.GetValueForOption(nameOpt)!;
            var type = context.ParseResult.GetValueForOption(typeOpt);
            var api = context.ParseResult.GetValueForOption(apiOpt);
            var apiKey = context.ParseResult.GetValueForOption(apiKeyOpt);
            var authentication = context.ParseResult.GetValueForOption(authenticationOpt);
            var clientId = context.ParseResult.GetValueForOption(clientIdOpt);
            var baseUrl = context.ParseResult.GetValueForOption(baseUrlOpt);
            var defaultModel = context.ParseResult.GetValueForOption(defaultModelOpt);
            var models = context.ParseResult.GetValueForOption(modelsOpt) ?? Array.Empty<string>();
            var disabled = context.ParseResult.GetValueForOption(disabledOpt);

            context.ExitCode = string.Equals(type, "microsoft-foundry", StringComparison.OrdinalIgnoreCase)
                ? await new ProviderCommand().ExecuteAddMicrosoftFoundryAsync(
                    configPath, name, baseUrl, authentication, clientId, apiKey, defaultModel, models,
                    enabled: !disabled, verbose, CancellationToken.None)
                : await new ProviderCommand().ExecuteAddAsync(
                    configPath, name, api, apiKey, baseUrl, defaultModel, models,
                    enabled: !disabled, verbose, CancellationToken.None);
        });

        return cmd;
    }

    private static Command BuildTestCommand()
    {
        var cmd = new Command("test", "Validate a provider through the running gateway's live registry and credential path.");
        var nameOpt = new Option<string>("--name", "Provider instance name to validate.") { IsRequired = true };
        var urlOpt = new Option<string>("--url", () => GatewayClientFactory.DefaultUrl, "Gateway base URL.");
        var tokenOpt = new Option<string?>("--token", "Gateway API credential. Required when --url is not the local gateway.");

        cmd.AddOption(nameOpt);
        cmd.AddOption(urlOpt);
        cmd.AddOption(tokenOpt);
        cmd.SetHandler(async context =>
        {
            var name = context.ParseResult.GetValueForOption(nameOpt)!;
            var url = context.ParseResult.GetValueForOption(urlOpt) ?? GatewayClientFactory.DefaultUrl;
            var token = context.ParseResult.GetValueForOption(tokenOpt);
            context.ExitCode = await ExecuteTestAsync(url, name, context.GetCancellationToken(), token);
        });

        return cmd;
    }

    private static Command BuildRemoveCommand(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var cmd = new Command("remove", "Remove a provider non-interactively.");
        var nameOpt = new Option<string>("--name", "Provider name to remove.") { IsRequired = true };

        cmd.AddOption(nameOpt);

        cmd.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            var name = context.ParseResult.GetValueForOption(nameOpt)!;

            context.ExitCode = await new ProviderCommand().ExecuteRemoveAsync(configPath, name, verbose, CancellationToken.None);
        });

        return cmd;
    }

    internal async Task<int> ExecuteAddAsync(
        string configPath,
        string name,
        string? api,
        string? apiKey,
        string? baseUrl,
        string? defaultModel,
        IReadOnlyCollection<string> models,
        bool enabled,
        bool verbose,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            AnsiConsole.MarkupLine("[red]--name is required.[/]");
            return 1;
        }

        var existed = (await CliConfigMutation.ReadAsync(configPath, cancellationToken))
            .FindEntryKey(ProvidersPath, name) is not null;

        // PATCH semantics: only the flags the caller actually supplied are written. Fields the CLI
        // does not model - reasoning, context capability, and anything a future schema adds - are
        // never mentioned and therefore survive verbatim (#2057).
        var patch = new ConfigValueMap()
            .Set("enabled", enabled)
            .SetIfNotNull("apiKey", apiKey)
            .SetIfNotNull("baseUrl", baseUrl)
            .SetIfNotNull("defaultModel", defaultModel)
            .SetIfNotNull("api", api);

        if (models.Count > 0)
            patch.Set("models", models.ToArray());

        var exitCode = await CliConfigMutation.ApplyAsync(
            configPath,
            document =>
            {
                if (!enabled)
                {
                    var dependentAgents = GetDependentAgents(document, name);
                    if (dependentAgents.Count > 0)
                        return FormatAssignedProviderError(name, dependentAgents, "disabling");
                }

                return document.TryPatchEntry(ProvidersPath, name, patch, out var error) ? null : error;
            },
            "before-provider-update",
            verbose,
            cancellationToken,
            // #2816: removing the last provider legitimately empties `providers`, so this command
            // names the one section it owns. It still cannot touch `channels`.
            namedSections: [ProvidersPath]);
        if (exitCode != 0)
            return exitCode;

        AnsiConsole.MarkupLine(existed
            ? $"[green]✓[/] Provider [green]{name}[/] updated."
            : $"[green]✓[/] Provider [green]{name}[/] added.");
        exitCode.PrintReceipt();
        PrintProviderActivationReceipt();

        if (verbose)
        {
            var saved = (await CliConfigMutation.ReadAsync(configPath, cancellationToken))
                .DescribeEntry(ProvidersPath, name);
            AnsiConsole.MarkupLine($"\n[dim]{CliText.SafeDisplay(saved ?? "{}")}[/]");
        }

        return 0;
    }

    internal async Task<int> ExecuteAddMicrosoftFoundryAsync(
        string configPath,
        string name,
        string? baseUrl,
        string? authenticationType,
        string? clientId,
        string? apiKey,
        string? defaultModel,
        IReadOnlyCollection<string> models,
        bool enabled,
        bool verbose,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            return WriteFoundryValidationError("--name is required.");
        if (!TryValidateFoundryEndpoint(baseUrl, out var endpointError))
            return WriteFoundryValidationError(endpointError);

        var normalizedAuthentication = authenticationType?.Trim().ToLowerInvariant();
        var authenticationError = normalizedAuthentication switch
        {
            "entra-default" when string.IsNullOrWhiteSpace(clientId) && string.IsNullOrWhiteSpace(apiKey) => null,
            "managed-identity" when string.IsNullOrWhiteSpace(clientId) && string.IsNullOrWhiteSpace(apiKey) => null,
            "user-assigned-managed-identity" when !string.IsNullOrWhiteSpace(clientId) && string.IsNullOrWhiteSpace(apiKey) => null,
            "api-key" when !string.IsNullOrWhiteSpace(apiKey) && string.IsNullOrWhiteSpace(clientId) => null,
            "user-assigned-managed-identity" => "--client-id is required for user-assigned managed identity, and --api-key is not allowed.",
            "api-key" => "--api-key is required for API-key authentication, and --client-id is not allowed.",
            "entra-default" or "managed-identity" => "--client-id and --api-key are not allowed for the selected Entra authentication mode.",
            _ => "--authentication must be entra-default, managed-identity, user-assigned-managed-identity, or api-key."
        };
        if (authenticationError is not null)
            return WriteFoundryValidationError(authenticationError);
        if (string.IsNullOrWhiteSpace(defaultModel) && models.Count == 0)
            return WriteFoundryValidationError("--default-model or at least one --model is required.");

        var resolvedModels = models.Count > 0 ? models.ToArray() : new[] { defaultModel! };
        var resolvedDefaultModel = string.IsNullOrWhiteSpace(defaultModel) ? resolvedModels[0] : defaultModel;
        var authentication = new ConfigValueMap().Set("type", normalizedAuthentication);
        if (!string.IsNullOrWhiteSpace(clientId))
            authentication.Set("clientId", clientId);

        var patch = new ConfigValueMap()
            .Set("type", "microsoft-foundry")
            .Set("enabled", enabled)
            .Set("api", "microsoft-foundry-responses")
            .Set("baseUrl", baseUrl!.TrimEnd('/'))
            .Set("authentication", authentication)
            .Set("apiKey", normalizedAuthentication == "api-key" ? apiKey : null)
            .Set("defaultModel", resolvedDefaultModel)
            .Set("models", resolvedModels);

        var existed = (await CliConfigMutation.ReadAsync(configPath, cancellationToken))
            .FindEntryKey(ProvidersPath, name) is not null;
        var exitCode = await CliConfigMutation.ApplyAsync(
            configPath,
            document => document.TryPatchEntry(ProvidersPath, name, patch, out var error) ? null : error,
            "before-provider-update",
            verbose,
            cancellationToken,
            namedSections: [ProvidersPath]);
        if (exitCode != 0)
            return exitCode;

        AnsiConsole.MarkupLine(existed
            ? $"[green]✓[/] Microsoft Foundry provider [green]{name}[/] updated."
            : $"[green]✓[/] Microsoft Foundry provider [green]{name}[/] added.");
        exitCode.PrintReceipt();
        PrintProviderActivationReceipt();
        return 0;
    }

    private static bool TryValidateFoundryEndpoint(string? baseUrl, out string error)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            !endpoint.IsDefaultPort ||
            endpoint.UserInfo.Length > 0 ||
            endpoint.Query.Length > 0 ||
            endpoint.Fragment.Length > 0 ||
            !string.Equals(endpoint.AbsolutePath.TrimEnd('/'), "/openai/v1", StringComparison.Ordinal))
        {
            error = "--base-url must be an HTTPS Microsoft Foundry inference endpoint ending in /openai/v1.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static int WriteFoundryValidationError(string error)
    {
        AnsiConsole.MarkupLine($"[red]{CliText.SafeDisplay(error)}[/]");
        return 1;
    }

    internal static void PrintProviderActivationReceipt()
    {
        AnsiConsole.MarkupLine("  Persistence: [green]succeeded[/].");
        AnsiConsole.MarkupLine("  Runtime activation: [yellow]not validated[/] by this offline command.");
        AnsiConsole.MarkupLine(
            "  Restart required: [green]no[/] when the running gateway receives the configuration reload; " +
            "verify the provider appears in its live model catalogue before assigning an agent.");
    }

    internal static async Task<int> ExecuteTestAsync(
        string baseUrl,
        string name,
        CancellationToken cancellationToken,
        string? token = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            AnsiConsole.MarkupLine("[red]--name is required.[/]");
            return 1;
        }

        var resolution = GatewayClientFactory.Resolve(
            baseUrl,
            TimeSpan.FromSeconds(15),
            token,
            GatewayClientFactory.DefaultCredentialSource());
        if (resolution.Client is null)
        {
            AnsiConsole.MarkupLine("[red]{0}[/]", CliText.SafeDisplay(resolution.RefusalMessage!));
            return 1;
        }

        using var client = resolution.Client;
        try
        {
            using var response = await client.GetAsync(
                $"/api/providers/{Uri.EscapeDataString(name)}/health",
                cancellationToken).ConfigureAwait(false);
            ProviderHealthReceipt? health = null;
            try
            {
                health = await response.Content.ReadFromJsonAsync<ProviderHealthReceipt>(
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
                // Error responses may use the gateway's ordinary string/problem-details shape.
                // Status is still authoritative; never mistake that body mismatch for readiness.
            }

            if (response.IsSuccessStatusCode && health is { Status: "healthy" })
            {
                AnsiConsole.MarkupLine(
                    "[green]✓[/] Provider [green]{0}[/] is active and validated by the running gateway.",
                    CliText.SafeDisplay(name));
                AnsiConsole.MarkupLine("  Models: {0}", health.Models);
                AnsiConsole.MarkupLine("  Credentials: [green]resolved[/]");
                return 0;
            }

            AnsiConsole.MarkupLine(
                "[red]Provider {0} is not ready in the running gateway.[/]",
                CliText.SafeDisplay(name));
            if (!string.IsNullOrWhiteSpace(health?.Error))
                AnsiConsole.MarkupLine("  {0}", CliText.SafeDisplay(health.Error));
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                AnsiConsole.MarkupLine("  The provider is absent from the live model registry; wait for configuration reload or inspect the running gateway configuration.");
            else
                AnsiConsole.MarkupLine("  Gateway health check returned HTTP {0}.", (int)response.StatusCode);
            return 1;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine(
                "[red]Cannot reach gateway at {0}:[/] {1}",
                CliText.SafeDisplay(GatewayDiagnosticsProjection.ProjectUrl(baseUrl)),
                CliText.SafeDisplay(GatewayDiagnosticsProjection.ProjectMessage(ex.Message)));
            return 1;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[red]Provider validation timed out.[/]");
            return 1;
        }
        catch (JsonException)
        {
            AnsiConsole.MarkupLine("[red]The running gateway returned an invalid provider health response.[/]");
            return 1;
        }
    }

    private sealed record ProviderHealthReceipt(string Status, int Models, bool HasCredentials, string? Error);

    /// <summary>Raw-document paths used by provider dependency checks and mutations.</summary>
    private const string ProvidersPath = "providers";
    private const string AgentsPath = "agents";

    private static IReadOnlyList<string> GetDependentAgents(ConfigDocument document, string providerName) =>
        document.GetEntryKeys(AgentsPath)
            .Where(agentName =>
            {
                var agentJson = document.DescribeEntry(AgentsPath, agentName);
                if (agentJson is null)
                    return false;

                using var agent = JsonDocument.Parse(agentJson);
                return agent.RootElement.TryGetProperty("provider", out var provider)
                       && provider.ValueKind == JsonValueKind.String
                       && string.Equals(provider.GetString(), providerName, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(agentName => agentName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string FormatAssignedProviderError(
        string providerName,
        IReadOnlyCollection<string> dependentAgents,
        string operation)
    {
        var agentList = string.Join(", ", dependentAgents.Select(CliText.SafeDisplay));
        return $"Provider '{CliText.SafeDisplay(providerName)}' is assigned to {dependentAgents.Count} agent(s): {agentList}. Reassign those agents before {operation} the provider.";
    }

    internal async Task<int> ExecuteRemoveAsync(string configPath, string name, bool verbose, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            AnsiConsole.MarkupLine("[red]--name is required.[/]");
            return 1;
        }

        var document = await CliConfigMutation.ReadAsync(configPath, cancellationToken);
        if (document.FindEntryKey(ProvidersPath, name) is null)
        {
            AnsiConsole.MarkupLine($"[yellow]No provider named '{CliText.SafeDisplay(name)}' to remove.[/]");
            return 0;
        }

        var exitCode = await CliConfigMutation.ApplyAsync(
            configPath,
            candidate =>
            {
                var dependentAgents = GetDependentAgents(candidate, name);
                if (dependentAgents.Count > 0)
                    return FormatAssignedProviderError(name, dependentAgents, "removing");

                return candidate.TryRemoveEntry(ProvidersPath, name, out var error) ? null : error;
            },
            "before-provider-update",
            verbose,
            cancellationToken,
            // #2816: removing the last provider legitimately empties `providers`, so this command
            // names the one section it owns. It still cannot touch `channels`.
            namedSections: [ProvidersPath]);
        if (exitCode != 0)
            return exitCode;

        AnsiConsole.MarkupLine($"[green]✓[/] Provider [green]{CliText.SafeDisplay(name)}[/] removed.");
        exitCode.PrintReceipt();
        if (verbose)
        {
            var remaining = (await CliConfigMutation.ReadAsync(configPath, cancellationToken))
                .CountEntries(ProvidersPath);
            AnsiConsole.MarkupLine($"[dim]Remaining providers: {remaining}[/]");
        }

        return 0;
    }

    internal async Task<int> ExecuteDefaultAsync(bool verbose, CancellationToken cancellationToken)
        => await ExecuteDefaultAsync(PlatformConfigLoader.DefaultConfigPath, PlatformConfigLoader.DefaultHomePath, verbose, cancellationToken);

    internal async Task<int> ExecuteDefaultAsync(string configPath, string home, bool verbose, CancellationToken cancellationToken)
    {
        var config = await LoadOrCreateConfigAsync(configPath, cancellationToken);
        var existingProviders = config.Providers?
            .Where(p => p.Value.Enabled)
            .Select(p => p.Key)
            .ToList() ?? [];

        if (existingProviders.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No providers configured.[/]");
            AnsiConsole.MarkupLine("Starting provider setup wizard...\n");
            return await ExecuteSetupAsync(configPath, home, verbose, cancellationToken);
        }

        return await ExecuteListAsync(configPath, verbose, cancellationToken);
    }

    internal async Task<int> ExecuteListAsync(bool verbose, CancellationToken cancellationToken)
        => await ExecuteListAsync(PlatformConfigLoader.DefaultConfigPath, verbose, cancellationToken);

    internal async Task<int> ExecuteListAsync(string configPath, bool verbose, CancellationToken cancellationToken)
    {
        var config = await LoadOrCreateConfigAsync(configPath, cancellationToken);
        if (config.Providers is null || config.Providers.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No providers configured.[/] Run [green]botnexus provider setup[/] to add one.");
            return 0;
        }

        var table = new Table()
            .AddColumn("Provider")
            .AddColumn("Enabled")
            .AddColumn("Auth")
            .AddColumn("Default Model")
            .AddColumn("Base URL");

        foreach (var (name, provider) in config.Providers)
        {
            var authDisplay = GetAuthDisplay(provider.ApiKey);
            table.AddRow(
                name,
                provider.Enabled ? "[green]Yes[/]" : "[red]No[/]",
                authDisplay,
                provider.ResolveChatDefaultModel() ?? "[dim]—[/]",
                provider.BaseUrl ?? "[dim]default[/]");
        }

        AnsiConsole.Write(table);
        return 0;
    }

    internal async Task<int> ExecuteSetupAsync(bool verbose, CancellationToken cancellationToken)
        => await ExecuteSetupAsync(PlatformConfigLoader.DefaultConfigPath, PlatformConfigLoader.DefaultHomePath, verbose, null, null, cancellationToken);

    internal async Task<int> ExecuteSetupAsync(string configPath, string home, bool verbose, CancellationToken cancellationToken)
        => await ExecuteSetupAsync(configPath, home, verbose, null, null, cancellationToken);

    internal async Task<int> ExecuteSetupAsync(string configPath, string home, bool verbose, string? preselectedProvider, CancellationToken cancellationToken)
        => await ExecuteSetupAsync(configPath, home, verbose, preselectedProvider, null, cancellationToken);

    internal async Task<int> ExecuteCopilotSetupAsync(
        string configPath,
        string home,
        bool verbose,
        string providerInstance,
        CancellationToken cancellationToken)
        => await ExecuteSetupAsync(
            configPath,
            home,
            verbose,
            CopilotAuthLoader.NormalizeProviderInstance(providerInstance),
            "github-copilot",
            cancellationToken);

    private async Task<int> ExecuteSetupAsync(
        string configPath,
        string home,
        bool verbose,
        string? preselectedProvider,
        string? providerType,
        CancellationToken cancellationToken)
    {
        if (preselectedProvider is not null)
        {
            if (providerType is null && !KnownProviders.Contains(preselectedProvider, StringComparer.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine($"[red]Unknown provider '{CliText.SafeDisplay(preselectedProvider)}'. Known providers: {string.Join(", ", KnownProviders)}.[/]");
                AnsiConsole.MarkupLine("[dim]For other providers (e.g. local OpenAI-compatible servers or 'integration-mock'), use [green]botnexus provider add[/].[/]");
                return 1;
            }
        }

        var config = await LoadOrCreateConfigAsync(configPath, cancellationToken);

        // Seed the wizard context with the loaded config
        var ctx = new WizardContext();
        ctx.Set("config", config);
        ctx.Set("verbose", verbose);
        ctx.Set("home", home);

        var wizardBuilder = new WizardBuilder();

        if (preselectedProvider is null)
        {
            wizardBuilder.AskSelection("pick-provider", "Which provider do you want to configure?", "provider",
                KnownProviders,
                p => ProviderDisplayNames.TryGetValue(p, out var display) ? display : p);
        }
        else
        {
            // Pre-seed the provider key and use a no-op action so the wizard stays linear.
            var resolved = providerType is null
                ? KnownProviders.First(p => string.Equals(p, preselectedProvider, StringComparison.OrdinalIgnoreCase))
                : preselectedProvider;
            ctx.Set("provider", resolved);
            if (providerType is not null)
                ctx.Set("providerType", providerType);
            wizardBuilder.Action("pick-provider", (_, _) => Task.CompletedTask);
        }

        var wizard = wizardBuilder
            .Action("show-provider", (c, _) =>
            {
                var name = c.Get<string>("provider");
                AnsiConsole.MarkupLine($"\nConfiguring [green]{name}[/]...\n");
                var effectiveType = c.TryGet<string>("providerType", out var type) ? type : name;
                c.Set("authMode", ProviderAuthModes.GetValueOrDefault(effectiveType, "apikey"));
                return Task.CompletedTask;
            })
            .Check("route-auth", (c, _) =>
            {
                var mode = c.Get<string>("authMode");
                return Task.FromResult(mode switch
                {
                    "oauth" => StepResult.GoTo("oauth-flow"),
                    "none" => StepResult.GoTo("ollama-setup"),
                    _ => StepResult.GoTo("ask-apikey")
                });
            })
            .AskText("ask-apikey", "Enter your API key:", "apiKey", secret: true,
                validator: key => string.IsNullOrWhiteSpace(key)
                    ? ValidationResult.Error("API key cannot be empty.")
                    : ValidationResult.Success())
            .Check("skip-oauth", (_, _) => Task.FromResult(StepResult.GoTo("pick-model")))
            .Step(new OAuthFlowStep())
            .Action("ollama-setup", (c, _) =>
            {
                var baseUrl = AnsiConsole.Prompt(
                    new TextPrompt<string>("Ollama server URL:")
                        .DefaultValue(OllamaProviderSubcommand.DefaultBaseUrl)
                        .Validate(url => Uri.IsWellFormedUriString(url, UriKind.Absolute)
                            ? ValidationResult.Success()
                            : ValidationResult.Error("Must be a valid URL.")));

                c.Set("ollamaBaseUrl", baseUrl);
                c.Set("apiKey", "ollama");
                c.Set("baseUrl", baseUrl.TrimEnd('/') + "/v1");
                c.Set("api", "openai-completions");
                AnsiConsole.MarkupLine($"[dim]Base URL: {CliText.SafeDisplay(GatewayDiagnosticsProjection.ProjectUrl(baseUrl))}, API: openai-completions[/]\n");
                return Task.CompletedTask;
            })
            .Step(new OllamaProviderSubcommand.OllamaPickModelStep())
            .Step(new PickModelStep())
            .Action("save", async (c, ct) =>
            {
                var cfg = c.Get<PlatformConfig>("config");
                var providerName = c.Get<string>("provider");
                var authMode = c.Get<string>("authMode");

                var apiKeyValue = authMode switch
                {
                    "oauth" => $"auth:{providerName}",
                    "none" => "ollama",
                    _ => c.Get<string>("apiKey")
                };

                var wizardPatch = new ConfigValueMap()
                    .Set("enabled", true)
                    .Set("apiKey", apiKeyValue);
                if (c.TryGet<string>("providerType", out var configuredType))
                    wizardPatch.Set("type", configuredType);
                if (c.TryGet<string>("baseUrl", out var baseUrl))
                    wizardPatch.Set("baseUrl", baseUrl);
                if (c.TryGet<string>("api", out var api))
                    wizardPatch.Set("api", api);
                if (c.TryGet<string>("defaultModel", out var model))
                    wizardPatch.Set("defaultModel", model);

                var wizardExit = await CliConfigMutation.ApplyAsync(
                    configPath,
                    document => document.TryPatchEntry(ProvidersPath, providerName, wizardPatch, out var error) ? null : error,
                    "before-provider-update",
                    c.Get<bool>("verbose"),
                    ct,
                    namedSections: [ProvidersPath]);
                if (wizardExit != 0)
                    return;

                AnsiConsole.MarkupLine($"[green]✓[/] Provider [green]{providerName}[/] configured successfully.");
                wizardExit.PrintReceipt();
                PrintProviderActivationReceipt();

                if (c.Get<bool>("verbose"))
                {
                    var saved = (await CliConfigMutation.ReadAsync(configPath, ct))
                        .DescribeEntry(ProvidersPath, providerName);
                    AnsiConsole.MarkupLine($"\n[dim]{CliText.SafeDisplay(saved ?? "{}")}[/]");
                }
            })
            .Build();

        var result = await wizard.RunAsync(ctx, cancellationToken);
        return result.Outcome == WizardOutcome.Completed ? 0 : 1;
    }

    /// <summary>
    /// Wizard step that runs the GitHub Copilot OAuth device code flow and saves
    /// credentials to auth.json.
    /// </summary>
    internal sealed class OAuthFlowStep : IWizardStep
    {
        private readonly Func<string, string, CancellationToken, Task<OAuthCredentials?>> _runOAuthFlow;

        public OAuthFlowStep(Func<string, string, CancellationToken, Task<OAuthCredentials?>>? runOAuthFlow = null)
        {
            _runOAuthFlow = runOAuthFlow ?? RunOAuthFlowAsync;
        }

        public string Name => "oauth-flow";

        public async Task<StepResult> ExecuteAsync(WizardContext context, CancellationToken cancellationToken)
        {
            var providerName = context.Get<string>("provider");
            var homePath = context.TryGet<string>("home", out var h) ? h : PlatformConfigLoader.DefaultHomePath;
            var credentials = await _runOAuthFlow(providerName, homePath, cancellationToken);
            if (credentials is null)
                return StepResult.Abort();

            // OAuth providers (e.g. GitHub Copilot) get their endpoint and API
            // from the built-in model registry, so jump straight to model
            // selection. Falling through to the next step would run the Ollama
            // setup, which overwrites baseUrl/api with local Ollama values.
            return StepResult.GoTo("pick-model");
        }
    }

    /// <summary>
    /// Wizard step that populates the model registry and prompts the user to pick
    /// a default model for the selected provider.
    /// </summary>
    private sealed class PickModelStep : IWizardStep
    {
        public string Name => "pick-model";

        public Task<StepResult> ExecuteAsync(WizardContext context, CancellationToken cancellationToken)
        {
            var providerName = context.Get<string>("provider");

            var modelRegistry = new ModelRegistry();
            var builtInModels = new BuiltInModels();
            if (context.TryGet<string>("providerType", out var providerType) &&
                string.Equals(providerType, "github-copilot", StringComparison.OrdinalIgnoreCase))
            {
                builtInModels.RegisterCopilotInstance(modelRegistry, providerName);
            }
            else
            {
                builtInModels.RegisterAll(modelRegistry);
            }

            var registryKey = GetModelRegistryKey(providerName);
            var availableModels = modelRegistry.GetModels(registryKey);

            if (availableModels.Count > 0)
            {
                var defaultModel = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select a default model:")
                        .PageSize(15)
                        .AddChoices(availableModels.Select(m => m.Id))
                        .UseConverter(id =>
                        {
                            var model = availableModels.FirstOrDefault(m => m.Id == id);
                            return model is not null
                                ? $"{model.Id} — {model.Name}"
                                : id;
                        }));

                AnsiConsole.MarkupLine($"Default model: [green]{defaultModel}[/]\n");
                context.Set("defaultModel", defaultModel);
            }

            return Task.FromResult(StepResult.Continue());
        }
    }

    private static async Task<OAuthCredentials?> RunOAuthFlowAsync(string providerName, CancellationToken cancellationToken)
        => await RunOAuthFlowAsync(providerName, PlatformConfigLoader.DefaultHomePath, cancellationToken);

    private static async Task<OAuthCredentials?> RunOAuthFlowAsync(string providerName, string homePath, CancellationToken cancellationToken)
    {
        OAuthCredentials? credentials = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Starting GitHub device code flow...", async ctx =>
            {
                credentials = await CopilotOAuth.LoginAsync(
                    onAuth: (verificationUri, userCode) =>
                    {
                        ctx.Status("Waiting for authorization...");
                        AnsiConsole.WriteLine();
                        AnsiConsole.Write(new Rule("[yellow]GitHub Authorization Required[/]"));
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine($"  1. Open: [link={verificationUri}]{verificationUri}[/]");
                        AnsiConsole.MarkupLine($"  2. Enter code: [bold green]{userCode}[/]");
                        AnsiConsole.WriteLine();
                        AnsiConsole.Write(new Rule());
                        AnsiConsole.WriteLine();
                        return Task.CompletedTask;
                    },
                    onProgress: message => ctx.Status(message),
                    ct: cancellationToken);
            });

        if (credentials is null)
        {
            AnsiConsole.MarkupLine("[red]OAuth flow failed — no credentials received.[/]");
            return null;
        }

        // Exchange for Copilot token to validate and get endpoint
        AnsiConsole.MarkupLine("[dim]Exchanging token for Copilot access...[/]");
        var refreshed = await CopilotOAuth.RefreshAsync(credentials, cancellationToken);

        // Save to auth.json
        SaveAuthEntry(providerName, refreshed, homePath);
        AnsiConsole.MarkupLine("[green]✓[/] OAuth credentials saved to auth.json\n");

        return refreshed;
    }

    private static void SaveAuthEntry(string providerName, OAuthCredentials credentials)
    {
        SaveAuthEntry(providerName, credentials, PlatformConfigLoader.DefaultHomePath);
    }

    internal static void SaveAuthEntry(string providerName, OAuthCredentials credentials, string homePath)
    {
        var authPath = Path.Combine(homePath, "auth.json");
        var entries = new Dictionary<string, AuthFileEntry>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(authPath))
        {
            try
            {
                var existingJson = File.ReadAllText(authPath);
                entries = JsonSerializer.Deserialize<Dictionary<string, AuthFileEntry>>(existingJson, ReadJsonOptions)
                    ?? new Dictionary<string, AuthFileEntry>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                // Corrupt file — start fresh
            }
        }

        entries[providerName] = new AuthFileEntry
        {
            Type = "oauth",
            Refresh = credentials.RefreshToken,
            Access = credentials.AccessToken,
            Expires = credentials.ExpiresAt * 1000, // Store as milliseconds (matches GatewayAuthManager format)
            Endpoint = credentials.ApiEndpoint
        };

        var directory = Path.GetDirectoryName(authPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(authPath, JsonSerializer.Serialize(entries, WriteJsonOptions));
        // #2392: auth.json holds OAuth refresh/access tokens - restrict to the owner.
        SecureFilePermissions.RestrictToOwner(authPath);
    }

    private static async Task<PlatformConfig> LoadOrCreateConfigAsync(CancellationToken cancellationToken)
        => await LoadOrCreateConfigAsync(PlatformConfigLoader.DefaultConfigPath, cancellationToken);

    private static async Task<PlatformConfig> LoadOrCreateConfigAsync(string configPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
            return new PlatformConfig();

        try
        {
            return PlatformConfigAccessor.Shared.Get(configPath);
        }
        catch
        {
            return new PlatformConfig();
        }
    }

    private static string GetAuthDisplay(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return "[dim]none[/]";
        if (apiKey.StartsWith("auth:", StringComparison.OrdinalIgnoreCase))
            return "[cyan]OAuth[/]";
        if (apiKey.Length > 8)
            return $"[green]{CliText.SafeDisplay(apiKey[..4])}...{CliText.SafeDisplay(apiKey[^4..])}[/]";
        return "[green]configured[/]";
    }

    private static string GetModelRegistryKey(string providerName) =>
        providerName switch
        {
            "copilot" => "github-copilot",
            _ => providerName
        };

    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Auth entry matching GatewayAuthManager's internal format for auth.json compatibility.
    /// </summary>
    internal sealed class AuthFileEntry
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "oauth";

        [JsonPropertyName("refresh")]
        public string Refresh { get; set; } = string.Empty;

        [JsonPropertyName("access")]
        public string Access { get; set; } = string.Empty;

        [JsonPropertyName("expires")]
        public long Expires { get; set; }

        [JsonPropertyName("endpoint")]
        public string? Endpoint { get; set; }
    }
}

