using System.CommandLine;
using System.Diagnostics;
using System.Net.Http.Headers;
using BotNexus.Agent.Providers.Copilot;
using BotNexus.Agent.Providers.Copilot.Completions;
using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Copilot.Responses;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace BotNexus.Cli.Commands.Provider;

/// <summary>
/// Builds the <c>botnexus provider copilot</c> subcommand group:
/// <c>login</c>, <c>whoami</c>, <c>models</c>, <c>quota</c>, <c>test</c>.
/// These commands give operators a fast diagnostic surface for the GitHub
/// Copilot integration without round-tripping through the gateway — useful for
/// debugging auth, listing entitled models, checking the current quota, and
/// confirming the carved-out providers can reach the upstream endpoints.
/// </summary>
internal static class CopilotProviderSubcommand
{
    private const string DefaultTestModel = "gpt-5-mini";

    /// <summary>
    /// Constructs the <c>copilot</c> command tree. <paramref name="setupAlias"/>
    /// is invoked by <c>copilot login</c> so the device-code flow stays
    /// authoritative in <see cref="ProviderCommand.ExecuteSetupAsync(string,string,bool,string?,CancellationToken)"/>
    /// — this subcommand contributes diagnostics, not new auth code paths.
    /// </summary>
    public static Command Build(Option<bool> verboseOption, Option<string?> targetOption, Func<string, string, bool, CancellationToken, Task<int>> setupAlias)
    {
        var copilot = new Command("copilot", "GitHub Copilot diagnostics and auth helpers.");

        copilot.AddCommand(BuildLogin(verboseOption, targetOption, setupAlias));
        copilot.AddCommand(BuildWhoami(targetOption));
        copilot.AddCommand(BuildModels(targetOption));
        copilot.AddCommand(BuildQuota(targetOption));
        copilot.AddCommand(BuildTest(targetOption));

        return copilot;
    }

    private static Command BuildLogin(
        Option<bool> verboseOption,
        Option<string?> targetOption,
        Func<string, string, bool, CancellationToken, Task<int>> setupAlias)
    {
        var cmd = new Command("login", "Authenticate to GitHub Copilot via device code flow (alias for `provider setup --provider github-copilot`).");
        cmd.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await setupAlias(configPath, home, verbose, CancellationToken.None);
        });
        return cmd;
    }

    private static Command BuildWhoami(Option<string?> targetOption)
    {
        var cmd = new Command("whoami", "Show the authenticated Copilot user, plan, endpoint, and token expiry.");
        cmd.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            context.ExitCode = await ExecuteWhoamiAsync(CliPaths.ResolveTarget(target), CancellationToken.None);
        });
        return cmd;
    }

    private static Command BuildModels(Option<string?> targetOption)
    {
        var cmd = new Command("models", "List the GitHub Copilot models the authenticated user is entitled to invoke.");
        cmd.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            context.ExitCode = await ExecuteModelsAsync(CliPaths.ResolveTarget(target), CancellationToken.None);
        });
        return cmd;
    }

    private static Command BuildQuota(Option<string?> targetOption)
    {
        var cmd = new Command("quota", "Show the current Copilot quota snapshots (chat, completions, premium interactions).");
        cmd.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            context.ExitCode = await ExecuteQuotaAsync(CliPaths.ResolveTarget(target), CancellationToken.None);
        });
        return cmd;
    }

    private static Command BuildTest(Option<string?> targetOption)
    {
        var modelOption = new Option<string>("--model", () => DefaultTestModel, $"Copilot model id to round-trip (default: {DefaultTestModel}).");
        var promptOption = new Option<string>("--prompt", () => "Respond with the single word: ok.", "Prompt to send.");
        var cmd = new Command("test", "Round-trip a single request through the carved-out Copilot provider to confirm end-to-end connectivity.");
        cmd.AddOption(modelOption);
        cmd.AddOption(promptOption);
        cmd.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var modelId = context.ParseResult.GetValueForOption(modelOption) ?? DefaultTestModel;
            var prompt = context.ParseResult.GetValueForOption(promptOption) ?? "Respond with the single word: ok.";
            context.ExitCode = await ExecuteTestAsync(CliPaths.ResolveTarget(target), modelId, prompt, CancellationToken.None);
        });
        return cmd;
    }

    private static async Task<int> ExecuteWhoamiAsync(string home, CancellationToken ct)
    {
        var auth = await CopilotAuthLoader.LoadAsync(home, ct);
        if (auth is null)
        {
            AnsiConsole.MarkupLine("[red]Not logged in.[/] Run [green]botnexus provider copilot login[/].");
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var client = new CopilotDiscoveryClient(http);
        CopilotUserInfo info;
        try
        {
            info = await client.GetUserAsync(auth.GitHubToken, ct);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to fetch Copilot user info:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumn("Field").AddColumn("Value");
        table.AddRow("Login", Markup.Escape(info.Login ?? "—"));
        table.AddRow("Plan", Markup.Escape(info.CopilotPlan ?? "—"));
        table.AddRow("SKU", Markup.Escape(info.AccessTypeSku ?? "—"));
        table.AddRow("Assigned", Markup.Escape(info.AssignedDate ?? "—"));
        table.AddRow("Chat enabled", info.ChatEnabled ? "[green]yes[/]" : "[red]no[/]");
        table.AddRow("CLI enabled", info.CliEnabled ? "[green]yes[/]" : "[red]no[/]");
        table.AddRow("Organizations", Markup.Escape(string.Join(", ", info.OrganizationLoginList ?? new List<string>())));
        table.AddRow("API endpoint", Markup.Escape(info.Endpoints?.Api ?? "—"));
        table.AddRow("Cached endpoint", Markup.Escape(auth.ApiEndpoint ?? "—"));

        var expiry = auth.ExpiresAtUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(auth.ExpiresAtUnixMs).ToString("yyyy-MM-ddTHH:mm:ssK")
            : "—";
        table.AddRow("Session token expiry", Markup.Escape(expiry));

        AnsiConsole.Write(table);
        return 0;
    }

    private static async Task<int> ExecuteModelsAsync(string home, CancellationToken ct)
    {
        var auth = await CopilotAuthLoader.LoadAsync(home, ct);
        if (auth is null)
        {
            AnsiConsole.MarkupLine("[red]Not logged in.[/] Run [green]botnexus provider copilot login[/].");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(auth.ApiEndpoint))
        {
            AnsiConsole.MarkupLine("[red]No Copilot API endpoint cached.[/] Run [green]botnexus provider copilot whoami[/] first.");
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var client = new CopilotDiscoveryClient(http);
        CopilotModelsResponse models;
        try
        {
            models = await client.GetModelsAsync(auth.ApiEndpoint, auth.CopilotSessionToken, ct);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to list Copilot models:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }

        var entries = models.Data ?? new List<CopilotModelInfo>();
        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No models returned.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Id")
            .AddColumn("Vendor")
            .AddColumn("Family")
            .AddColumn("Streaming")
            .AddColumn("Tools")
            .AddColumn("Vision")
            .AddColumn("Premium");

        foreach (var m in entries.OrderBy(m => m.Vendor).ThenBy(m => m.Id))
        {
            table.AddRow(
                Markup.Escape(m.Id ?? "—"),
                Markup.Escape(m.Vendor ?? "—"),
                Markup.Escape(m.Capabilities?.Family ?? "—"),
                Bool(m.Capabilities?.Supports?.Streaming),
                Bool(m.Capabilities?.Supports?.ToolCalls),
                Bool(m.Capabilities?.Supports?.Vision),
                Bool(m.Billing?.IsPremium));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]{entries.Count} models from {Markup.Escape(auth.ApiEndpoint)}[/]");
        return 0;
    }

    private static async Task<int> ExecuteQuotaAsync(string home, CancellationToken ct)
    {
        var auth = await CopilotAuthLoader.LoadAsync(home, ct);
        if (auth is null)
        {
            AnsiConsole.MarkupLine("[red]Not logged in.[/] Run [green]botnexus provider copilot login[/].");
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var client = new CopilotDiscoveryClient(http);
        CopilotUserInfo info;
        try
        {
            info = await client.GetUserAsync(auth.GitHubToken, ct);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to fetch Copilot quota:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }

        var snapshots = info.QuotaSnapshots ?? new Dictionary<string, CopilotQuotaSnapshot>();
        if (snapshots.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No quota snapshots reported.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Quota")
            .AddColumn("Remaining %")
            .AddColumn("Remaining")
            .AddColumn("Entitlement")
            .AddColumn("Overage")
            .AddColumn("Unlimited");

        foreach (var (key, snap) in snapshots.OrderBy(kv => kv.Key))
        {
            var pct = snap.PercentRemaining;
            var colour = pct switch
            {
                >= 50 => "green",
                >= 20 => "yellow",
                _ => "red"
            };
            table.AddRow(
                Markup.Escape(key),
                $"[{colour}]{pct:0.0}%[/]",
                snap.QuotaRemaining.ToString("0.##"),
                snap.Entitlement.ToString("0.##"),
                $"{snap.OverageCount:0.##}{(snap.OveragePermitted ? " (permitted)" : "")}",
                snap.Unlimited ? "[green]yes[/]" : "no");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]Quota resets: {Markup.Escape(info.QuotaResetDate ?? "—")}[/]");
        return 0;
    }

    private static async Task<int> ExecuteTestAsync(string home, string modelId, string prompt, CancellationToken ct)
    {
        var auth = await CopilotAuthLoader.LoadAsync(home, ct);
        if (auth is null)
        {
            AnsiConsole.MarkupLine("[red]Not logged in.[/] Run [green]botnexus provider copilot login[/].");
            return 1;
        }

        // #1639: register the models with the account's resolved endpoint so the model is born with
        // the correct host (enterprise vs individual). The carved-out providers read BaseUrl off the
        // model, so no post-hoc BaseUrl patch is needed here anymore.
        var registry = new ModelRegistry();
        new BuiltInModels().RegisterAll(registry, providerKey =>
            providerKey == "github-copilot" && !string.IsNullOrWhiteSpace(auth.ApiEndpoint)
                ? auth.ApiEndpoint
                : null);
        var model = registry.GetModel("github-copilot", modelId);
        if (model is null)
        {
            AnsiConsole.MarkupLine($"[red]Unknown Copilot model:[/] {Markup.Escape(modelId)}");
            AnsiConsole.MarkupLine("Run [green]botnexus provider copilot models[/] to see what your account is entitled to.");
            return 1;
        }

        var context = new Context(
            SystemPrompt: null,
            Messages: new Message[]
            {
                new UserMessage(prompt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            });

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        IApiProvider provider;
        StreamOptions options;
        switch (model.Api)
        {
            case CopilotMessagesProvider.ApiId:
                provider = new CopilotMessagesProvider(http);
                options = new CopilotMessagesOptions { ApiKey = auth.CopilotSessionToken, CancellationToken = ct };
                break;
            case "github-copilot-responses":
                provider = new CopilotResponsesProvider(http, NullLogger<CopilotResponsesProvider>.Instance);
                options = new CopilotResponsesOptions { ApiKey = auth.CopilotSessionToken, CancellationToken = ct };
                break;
            case "github-copilot-completions":
                provider = new CopilotCompletionsProvider(http, NullLogger<CopilotCompletionsProvider>.Instance);
                options = new CopilotCompletionsOptions { ApiKey = auth.CopilotSessionToken, CancellationToken = ct };
                break;
            default:
                AnsiConsole.MarkupLine($"[red]Model '{Markup.Escape(modelId)}' is not registered against a Copilot API ({Markup.Escape(model.Api)}).[/]");
                return 1;
        }

        AnsiConsole.MarkupLine($"[dim]→ {Markup.Escape(model.Api)} | {Markup.Escape(model.Id)} | {Markup.Escape(model.BaseUrl)}[/]");

        var sw = Stopwatch.StartNew();
        long? firstTokenMs = null;
        var collected = new List<string>();
        try
        {
            var stream = provider.Stream(model, context, options);
            await foreach (var evt in stream.WithCancellation(ct))
            {
                switch (evt)
                {
                    case TextDeltaEvent delta:
                        firstTokenMs ??= sw.ElapsedMilliseconds;
                        collected.Add(delta.Delta);
                        break;
                    case ErrorEvent err:
                        AnsiConsole.MarkupLine($"[red]Stream error:[/] {Markup.Escape(err.Error.ErrorMessage ?? "unknown")}");
                        return 2;
                    case DoneEvent:
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Provider call failed:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }

        sw.Stop();
        var text = string.Concat(collected).Trim();
        AnsiConsole.MarkupLine($"[green]✓[/] Round-trip succeeded in {sw.ElapsedMilliseconds} ms (first token: {(firstTokenMs?.ToString() ?? "—")} ms).");
        if (!string.IsNullOrWhiteSpace(text))
        {
            AnsiConsole.MarkupLine($"[dim]Reply:[/] {Markup.Escape(text)}");
        }
        return 0;
    }

    private static string Bool(bool? value) => value switch
    {
        true => "[green]yes[/]",
        false => "no",
        _ => "[dim]—[/]"
    };
}
