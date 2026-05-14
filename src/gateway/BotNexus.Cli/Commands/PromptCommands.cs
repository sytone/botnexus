using System.CommandLine;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Cron;
using BotNexus.Cron.Prompts;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

internal sealed class PromptCommands
{
    public Command Build(Option<bool> verboseOption)
    {
        var prompt = new Command("prompt", "Manage prompt templates.");

        var targetOption = new Option<string?>("--target", () => null, "BotNexus home directory (config, workspace, extensions). Defaults to ~/.botnexus.");
        var configOption = new Option<string?>("--config", () => null, "Explicit config.json path. Overrides --target.");
        var agentOption = new Option<string?>("--agent", () => null, "Agent ID. Defaults to gateway.defaultAgentId.");
        var parameterOption = new Option<string[]>("--param", "Template parameter as key=value. Repeat for multiple values.")
        {
            AllowMultipleArgumentsPerToken = true
        };

        var listCommand = new Command("list", "List prompt templates.")
        {
            targetOption,
            configOption,
            agentOption
        };
        listCommand.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var configPath = ResolveConfigPath(context.ParseResult.GetValueForOption(configOption), target);
            var agentId = context.ParseResult.GetValueForOption(agentOption);
            context.ExitCode = await ExecuteListAsync(configPath, agentId, verbose, CancellationToken.None);
        });

        var templateArgument = new Argument<string>("template", "Template name.");

        var renderCommand = new Command("render", "Render a prompt template.")
        {
            templateArgument,
            targetOption,
            configOption,
            agentOption,
            parameterOption
        };
        renderCommand.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var configPath = ResolveConfigPath(context.ParseResult.GetValueForOption(configOption), target);
            var templateName = context.ParseResult.GetValueForArgument(templateArgument);
            var rawParameters = context.ParseResult.GetValueForOption(parameterOption) ?? [];
            var agentId = context.ParseResult.GetValueForOption(agentOption);
            context.ExitCode = await ExecuteRenderAsync(
                configPath,
                agentId ?? string.Empty,
                templateName,
                rawParameters,
                verbose,
                runMode: false,
                CancellationToken.None);
        });

        var sessionOption = new Option<string?>("--session", () => null, "Optional session ID when invoking the gateway.");
        var gatewayUrlOption = new Option<string?>("--gateway-url", () => null, "Override gateway URL (defaults to gateway.listenUrl).");
        var runCommand = new Command("run", "Render and run a prompt template.")
        {
            templateArgument,
            targetOption,
            configOption,
            agentOption,
            parameterOption,
            sessionOption,
            gatewayUrlOption
        };
        runCommand.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var configPath = ResolveConfigPath(context.ParseResult.GetValueForOption(configOption), target);
            var templateName = context.ParseResult.GetValueForArgument(templateArgument);
            var rawParameters = context.ParseResult.GetValueForOption(parameterOption) ?? [];
            var agentId = context.ParseResult.GetValueForOption(agentOption);
            var sessionId = context.ParseResult.GetValueForOption(sessionOption);
            var gatewayUrl = context.ParseResult.GetValueForOption(gatewayUrlOption);
            context.ExitCode = await ExecuteRunAsync(
                configPath,
                agentId,
                templateName,
                rawParameters,
                sessionId,
                gatewayUrl,
                verbose,
                CancellationToken.None);
        });

        prompt.AddCommand(listCommand);
        prompt.AddCommand(renderCommand);
        prompt.AddCommand(runCommand);
        return prompt;
    }

    public static bool TryParseParameters(
        IReadOnlyList<string> rawParameters,
        out Dictionary<string, string?> parameters,
        out string? error)
    {
        parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        error = null;

        foreach (var raw in rawParameters)
        {
            var separatorIndex = raw.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == raw.Length - 1)
            {
                error = "Invalid parameter format. Use --param key=value.";
                parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                return false;
            }

            var key = raw[..separatorIndex].Trim();
            var value = raw[(separatorIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                error = "Invalid parameter format. Use --param key=value.";
                parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                return false;
            }

            parameters[key] = value;
        }

        return true;
    }

    public async Task<int> ExecuteRenderAsync(
        string configPath,
        string agentId,
        string templateName,
        IReadOnlyList<string> rawParameters,
        bool verbose,
        bool runMode,
        CancellationToken cancellationToken)
    {
        _ = agentId;
        _ = verbose;
        _ = runMode;

        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Config file not found at [dim]{Markup.Escape(configPath)}[/].");
            return 1;
        }

        if (!TryParseParameters(rawParameters, out var parameters, out var parseError))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(parseError ?? "Invalid parameter format.")}");
            return 1;
        }

        var config = await LoadConfigAsync(configPath, cancellationToken);
        if (config is null)
            return 1;

        var resolvedAgentId = ResolveAgentId(agentId, config);
        if (string.IsNullOrWhiteSpace(resolvedAgentId))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Agent id is required. Pass --agent or set gateway.defaultAgentId.");
            return 1;
        }

        var homePath = ResolveHomePath(configPath);
        var priorHome = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", homePath);
        try
        {
            var resolver = CreateTemplateResolver(config);
            if (!resolver.TryRender(resolvedAgentId, templateName, parameters, out var rendered, out var error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(error)}");
                return 1;
            }

            Console.WriteLine(rendered);
            return 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOTNEXUS_HOME", priorHome);
        }
    }

    public async Task<int> ExecuteListAsync(
        string configPath,
        string? agentId,
        bool verbose,
        CancellationToken cancellationToken)
    {
        _ = verbose;
        _ = cancellationToken;
        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Config file not found at [dim]{Markup.Escape(configPath)}[/].");
            return 1;
        }

        var config = await LoadConfigAsync(configPath, cancellationToken);
        if (config is null)
            return 1;

        var resolvedAgentId = ResolveAgentId(agentId, config);
        if (string.IsNullOrWhiteSpace(resolvedAgentId))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Agent id is required. Pass --agent or set gateway.defaultAgentId.");
            return 1;
        }

        var homePath = ResolveHomePath(configPath);
        var priorHome = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", homePath);
        try
        {
            var resolver = CreateTemplateResolver(config);
            var templates = resolver.ListTemplateNames(resolvedAgentId);
            foreach (var template in templates)
                Console.WriteLine(template);
            return 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOTNEXUS_HOME", priorHome);
        }
    }

    public async Task<int> ExecuteRunAsync(
        string configPath,
        string? agentId,
        string templateName,
        IReadOnlyList<string> rawParameters,
        string? sessionId,
        string? gatewayUrlOverride,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var config = await LoadConfigAsync(configPath, cancellationToken);
        if (config is null)
            return 1;

        var resolvedAgentId = ResolveAgentId(agentId, config);
        if (string.IsNullOrWhiteSpace(resolvedAgentId))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Agent id is required. Pass --agent or set gateway.defaultAgentId.");
            return 1;
        }

        if (!TryParseParameters(rawParameters, out var parameters, out var parseError))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(parseError ?? "Invalid parameter format.")}");
            return 1;
        }

        var homePath = ResolveHomePath(configPath);
        var priorHome = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", homePath);
        string renderedPrompt;
        try
        {
            var resolver = CreateTemplateResolver(config);
            if (!resolver.TryRender(resolvedAgentId, templateName, parameters, out renderedPrompt, out var renderError))
            {
                if (!string.IsNullOrWhiteSpace(renderError))
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(renderError)}");
                return 1;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOTNEXUS_HOME", priorHome);
        }

        var gatewayUrl = string.IsNullOrWhiteSpace(gatewayUrlOverride)
            ? config.Gateway?.ListenUrl
            : gatewayUrlOverride;
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            gatewayUrl = "http://localhost:5000";

        var endpoint = $"{gatewayUrl.TrimEnd('/')}/api/chat";
        using var httpClient = new HttpClient();
        var response = await httpClient.PostAsJsonAsync(
            endpoint,
            new ChatRequestPayload(resolvedAgentId, renderedPrompt, sessionId),
            cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Gateway returned {(int)response.StatusCode}.");
            if (!string.IsNullOrWhiteSpace(responseText))
                AnsiConsole.WriteLine(responseText);
            return 1;
        }

        if (verbose)
            AnsiConsole.MarkupLine($"[dim]Rendered template {Markup.Escape(templateName)} and invoked {Markup.Escape(endpoint)}[/]");

        var chatResponse = JsonSerializer.Deserialize<ChatResponsePayload>(responseText, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        Console.WriteLine(chatResponse?.Content ?? responseText);
        return 0;
    }

    private static string ResolveConfigPath(string? explicitConfigPath, string? targetHome)
        => !string.IsNullOrWhiteSpace(explicitConfigPath)
            ? Path.GetFullPath(explicitConfigPath)
            : Path.Combine(CliPaths.ResolveTarget(targetHome), "config.json");

    private static string ResolveHomePath(string configPath)
        => Path.GetDirectoryName(Path.GetFullPath(configPath))
           ?? PlatformConfigLoader.DefaultHomePath;

    private static string? ResolveAgentId(string? agentId, PlatformConfig config)
        => string.IsNullOrWhiteSpace(agentId) ? config.Gateway?.DefaultAgentId : agentId;

    private static IPromptTemplateResolver CreateTemplateResolver(PlatformConfig config)
    {
        var cronOptions = new CronOptions
        {
            PromptTemplates = config.PromptTemplates?.ToDictionary(
                pair => pair.Key,
                pair => new ConfiguredPromptTemplate
                {
                    Prompt = pair.Value.Prompt,
                    Description = pair.Value.Description,
                    Defaults = pair.Value.Defaults?.ToDictionary(
                        entry => entry.Key,
                        entry => (string?)entry.Value,
                        StringComparer.OrdinalIgnoreCase),
                    Parameters = pair.Value.Parameters?.ToDictionary(
                        entry => entry.Key,
                        entry => new ConfiguredPromptTemplateParameter
                        {
                            Description = entry.Value.Description,
                            Default = entry.Value.Default,
                            Required = entry.Value.Required
                        },
                        StringComparer.OrdinalIgnoreCase)
                },
                StringComparer.OrdinalIgnoreCase)
        };

        return new CronOptionsPromptTemplateResolver(new StaticOptionsMonitor<CronOptions>(cronOptions));
    }

    private static async Task<PlatformConfig?> LoadConfigAsync(string configPath, CancellationToken cancellationToken)
    {
        try
        {
            var configJson = await File.ReadAllTextAsync(configPath, cancellationToken);
            return JsonSerializer.Deserialize<PlatformConfig>(configJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Unable to load config: {Markup.Escape(ex.Message)}");
            return null;
        }
    }

    private sealed class StaticOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
        where TOptions : class
    {
        private readonly TOptions _value;

        public StaticOptionsMonitor(TOptions value)
        {
            _value = value;
        }

        public TOptions CurrentValue => _value;

        public TOptions Get(string? name) => _value;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }

    private sealed record ChatRequestPayload(string AgentId, string Message, string? SessionId);

    private sealed record ChatResponsePayload(string SessionId, string Content);
}
