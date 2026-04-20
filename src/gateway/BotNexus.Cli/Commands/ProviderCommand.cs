using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Agent.Providers.Copilot;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Cli.Wizard;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

internal sealed class ProviderCommand
{
    private static readonly string[] KnownProviders = ["github-copilot", "openai", "anthropic"];

    private static readonly Dictionary<string, string> ProviderDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["github-copilot"] = "GitHub Copilot (OAuth — free with GitHub account)",
        ["openai"] = "OpenAI (API key required)",
        ["anthropic"] = "Anthropic (API key required)"
    };

    private static readonly Dictionary<string, string> ProviderAuthModes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["github-copilot"] = "oauth",
        ["openai"] = "apikey",
        ["anthropic"] = "apikey"
    };

    public Command Build(Option<bool> verboseOption)
    {
        var command = new Command("provider", "Configure and authenticate LLM providers.");

        var setupCommand = new Command("setup", "Interactively add and authenticate a new provider.");
        setupCommand.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteSetupAsync(verbose, CancellationToken.None);
        });

        var listCommand = new Command("list", "List configured providers.");
        listCommand.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteListAsync(verbose, CancellationToken.None);
        });

        command.AddCommand(setupCommand);
        command.AddCommand(listCommand);

        // Default to setup when no subcommand given
        command.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteDefaultAsync(verbose, CancellationToken.None);
        });

        return command;
    }

    internal async Task<int> ExecuteDefaultAsync(bool verbose, CancellationToken cancellationToken)
    {
        var config = await LoadOrCreateConfigAsync(cancellationToken);
        var existingProviders = config.Providers?
            .Where(p => p.Value.Enabled)
            .Select(p => p.Key)
            .ToList() ?? [];

        if (existingProviders.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No providers configured.[/]");
            AnsiConsole.MarkupLine("Starting provider setup wizard...\n");
            return await ExecuteSetupAsync(verbose, cancellationToken);
        }

        return await ExecuteListAsync(verbose, cancellationToken);
    }

    internal async Task<int> ExecuteListAsync(bool verbose, CancellationToken cancellationToken)
    {
        var config = await LoadOrCreateConfigAsync(cancellationToken);
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
                provider.DefaultModel ?? "[dim]—[/]",
                provider.BaseUrl ?? "[dim]default[/]");
        }

        AnsiConsole.Write(table);
        return 0;
    }

    internal async Task<int> ExecuteSetupAsync(bool verbose, CancellationToken cancellationToken)
    {
        var config = await LoadOrCreateConfigAsync(cancellationToken);

        // Seed the wizard context with the loaded config
        var ctx = new WizardContext();
        ctx.Set("config", config);
        ctx.Set("verbose", verbose);

        var wizard = new WizardBuilder()
            .AskSelection("pick-provider", "Which provider do you want to configure?", "provider",
                KnownProviders,
                p => ProviderDisplayNames.TryGetValue(p, out var display) ? display : p)
            .Action("show-provider", (c, _) =>
            {
                var name = c.Get<string>("provider");
                AnsiConsole.MarkupLine($"\nConfiguring [green]{name}[/]...\n");
                c.Set("authMode", ProviderAuthModes.GetValueOrDefault(name, "apikey"));
                return Task.CompletedTask;
            })
            .Check("route-auth", (c, _) =>
            {
                var mode = c.Get<string>("authMode");
                return Task.FromResult(mode == "oauth"
                    ? StepResult.GoTo("oauth-flow")
                    : StepResult.GoTo("ask-apikey"));
            })
            .AskText("ask-apikey", "Enter your API key:", "apiKey", secret: true,
                validator: key => string.IsNullOrWhiteSpace(key)
                    ? ValidationResult.Error("API key cannot be empty.")
                    : ValidationResult.Success())
            .Check("skip-oauth", (_, _) => Task.FromResult(StepResult.GoTo("pick-model")))
            .Step(new OAuthFlowStep())
            .Step(new PickModelStep())
            .Action("save", async (c, ct) =>
            {
                var cfg = c.Get<PlatformConfig>("config");
                var providerName = c.Get<string>("provider");
                var authMode = c.Get<string>("authMode");

                var apiKeyValue = authMode == "oauth"
                    ? $"auth:{providerName}"
                    : c.Get<string>("apiKey");

                cfg.Providers ??= new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase);
                cfg.Providers[providerName] = new ProviderConfig
                {
                    Enabled = true,
                    ApiKey = apiKeyValue,
                    DefaultModel = c.TryGet<string>("defaultModel", out var model) ? model : null
                };

                await SaveConfigAsync(cfg, ct);

                AnsiConsole.MarkupLine($"[green]✓[/] Provider [green]{providerName}[/] configured successfully.");
                AnsiConsole.MarkupLine($"  Config saved to: {PlatformConfigLoader.DefaultConfigPath}");

                if (c.Get<bool>("verbose"))
                {
                    var json = JsonSerializer.Serialize(cfg.Providers[providerName], WriteJsonOptions);
                    AnsiConsole.MarkupLine($"\n[dim]{Markup.Escape(json)}[/]");
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
    private sealed class OAuthFlowStep : IWizardStep
    {
        public string Name => "oauth-flow";

        public async Task<StepResult> ExecuteAsync(WizardContext context, CancellationToken cancellationToken)
        {
            var providerName = context.Get<string>("provider");
            var credentials = await RunOAuthFlowAsync(providerName, cancellationToken);
            if (credentials is null)
                return StepResult.Abort();

            return StepResult.Continue();
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
            new BuiltInModels().RegisterAll(modelRegistry);

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
        SaveAuthEntry(providerName, refreshed);
        AnsiConsole.MarkupLine("[green]✓[/] OAuth credentials saved to auth.json\n");

        return refreshed;
    }

    private static void SaveAuthEntry(string providerName, OAuthCredentials credentials)
    {
        var authPath = Path.Combine(PlatformConfigLoader.DefaultHomePath, "auth.json");
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
    }

    private static async Task<PlatformConfig> LoadOrCreateConfigAsync(CancellationToken cancellationToken)
    {
        var configPath = PlatformConfigLoader.DefaultConfigPath;
        if (!File.Exists(configPath))
            return new PlatformConfig();

        try
        {
            return await PlatformConfigLoader.LoadAsync(configPath, cancellationToken, validateOnLoad: false);
        }
        catch
        {
            return new PlatformConfig();
        }
    }

    private static async Task SaveConfigAsync(PlatformConfig config, CancellationToken cancellationToken)
    {
        var configPath = PlatformConfigLoader.DefaultConfigPath;
        PlatformConfigLoader.EnsureConfigDirectory();
        var json = JsonSerializer.Serialize(config, WriteJsonOptions);
        await File.WriteAllTextAsync(configPath, json, cancellationToken);
    }

    private static string GetAuthDisplay(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return "[dim]none[/]";
        if (apiKey.StartsWith("auth:", StringComparison.OrdinalIgnoreCase))
            return "[cyan]OAuth[/]";
        if (apiKey.Length > 8)
            return $"[green]{Markup.Escape(apiKey[..4])}...{Markup.Escape(apiKey[^4..])}[/]";
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
