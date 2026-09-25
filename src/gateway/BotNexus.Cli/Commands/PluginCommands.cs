using System.CommandLine;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Cli.Services;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>Manages plugins through the running gateway's extension-owned HTTP API.</summary>
internal sealed class PluginCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly HttpClient? _http;

    /// <summary>Creates commands using an injected client, primarily for focused transport tests.</summary>
    public PluginCommands(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>Creates commands using clients resolved by the gateway credential-policy factory.</summary>
    public PluginCommands() { }

    /// <summary>Builds the <c>plugin</c> command group.</summary>
    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var command = new Command("plugin", "Manage plugins through the gateway API.");
        var urlOption = new Option<string>("--url", () => GatewayClientFactory.DefaultUrl, "Gateway base URL.");
        var tokenOption = new Option<string?>("--token", "Gateway API credential. Required when --url is not the local gateway.");
        command.AddOption(urlOption);
        command.AddOption(tokenOption);

        var list = new Command("list", "List installed plugins.");
        list.SetHandler(context => ExecuteAsync(context, urlOption, tokenOption, targetOption, HttpMethod.Get, "api/plugins", null));

        var getName = new Argument<string>("name", "Plugin name.");
        var get = new Command("get", "Show an installed plugin.") { getName };
        get.SetHandler(context => ExecuteAsync(context, urlOption, tokenOption, targetOption, HttpMethod.Get,
            $"api/plugins/{Escape(context.ParseResult.GetValueForArgument(getName))}", null));

        var source = new Argument<string>("source", "Git source URL.");
        var reference = new Option<string?>("--reference", "Branch, tag, or commit to install.");
        var pin = new Option<bool>("--pin", "Disable updates after installation.");
        var install = new Command("install", "Install a plugin from a git source.") { source, reference, pin };
        install.SetHandler(context => ExecuteAsync(
            context,
            urlOption,
            tokenOption,
            targetOption,
            HttpMethod.Post,
            "api/plugins",
            new PluginInstallWireRequest(
                context.ParseResult.GetValueForArgument(source),
                context.ParseResult.GetValueForOption(reference),
                !context.ParseResult.GetValueForOption(pin))));

        command.AddCommand(list);
        command.AddCommand(get);
        command.AddCommand(install);
        command.AddCommand(BuildNamed("update", "Update a plugin from its recorded source.", HttpMethod.Post, "update", urlOption, tokenOption, targetOption));
        command.AddCommand(BuildNamed("remove", "Remove an installed plugin.", HttpMethod.Delete, null, urlOption, tokenOption, targetOption));
        command.AddCommand(BuildNamed("pin", "Disable updates for a plugin.", HttpMethod.Post, "pin", urlOption, tokenOption, targetOption));
        command.AddCommand(BuildNamed("unpin", "Enable updates for a plugin.", HttpMethod.Post, "unpin", urlOption, tokenOption, targetOption));
        return command;
    }

    private Command BuildNamed(
        string name,
        string description,
        HttpMethod method,
        string? suffix,
        Option<string> urlOption,
        Option<string?> tokenOption,
        Option<string?> targetOption)
    {
        var pluginName = new Argument<string>("name", "Plugin name.");
        var command = new Command(name, description) { pluginName };
        command.SetHandler(context =>
        {
            var path = $"api/plugins/{Escape(context.ParseResult.GetValueForArgument(pluginName))}";
            if (suffix is not null)
            {
                path += $"/{suffix}";
            }

            return ExecuteAsync(context, urlOption, tokenOption, targetOption, method, path, null);
        });
        return command;
    }

    private async Task ExecuteAsync(
        System.CommandLine.Invocation.InvocationContext context,
        Option<string> urlOption,
        Option<string?> tokenOption,
        Option<string?> targetOption,
        HttpMethod method,
        string path,
        object? body)
    {
        var url = context.ParseResult.GetValueForOption(urlOption) ?? GatewayClientFactory.DefaultUrl;
        HttpClient? resolvedClient = null;
        var client = _http;
        string? refusal;
        if (client is null)
        {
            var resolution = GatewayClientFactory.Resolve(
                url,
                TimeSpan.FromSeconds(100),
                context.ParseResult.GetValueForOption(tokenOption),
                GatewayClientFactory.DefaultCredentialSource(context.ParseResult.GetValueForOption(targetOption)));
            resolvedClient = resolution.Client;
            client = resolvedClient;
            refusal = resolution.RefusalMessage;
        }
        else
        {
            refusal = GatewayClientFactory.ApplyPolicy(
                client,
                url,
                context.ParseResult.GetValueForOption(tokenOption),
                GatewayClientFactory.DefaultCredentialSource(context.ParseResult.GetValueForOption(targetOption)));
        }

        if (client is null || refusal is not null)
        {
            resolvedClient?.Dispose();
            AnsiConsole.MarkupLine("[red]{0}[/]", CliText.SafeDisplay(refusal ?? "Gateway client resolution failed."));
            context.ExitCode = 1;
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: JsonOptions);
            }

            using var response = await client.SendAsync(request, context.GetCancellationToken()).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(context.GetCancellationToken()).ConfigureAwait(false);
            WriteStructuredJson(text, response.StatusCode);
            context.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] {0}", CliText.SafeDisplay(ex.Message));
            context.ExitCode = 1;
        }
        finally
        {
            resolvedClient?.Dispose();
        }
    }

    private static void WriteStructuredJson(string payload, System.Net.HttpStatusCode statusCode)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(new { statusCode = (int)statusCode }, JsonOptions));
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            AnsiConsole.WriteLine(JsonSerializer.Serialize(document.RootElement, JsonOptions));
        }
        catch (JsonException)
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(
                new { statusCode = (int)statusCode, error = payload },
                JsonOptions));
        }
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private sealed record PluginInstallWireRequest(string Source, string? Reference, bool UpdatesEnabled);
}
