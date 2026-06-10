using System.CommandLine;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Cli.Wizard;
using Spectre.Console;

namespace BotNexus.Cli.Commands.Provider;

/// <summary>
/// Builds the <c>botnexus provider ollama</c> subcommand group:
/// <c>models</c>, <c>test</c>, <c>status</c>.
/// Provides operator diagnostics for local Ollama instances without
/// requiring gateway round-trips — useful for verifying connectivity,
/// listing pulled models, and confirming tool-call support.
/// </summary>
internal static class OllamaProviderSubcommand
{
    internal const string DefaultBaseUrl = "http://localhost:11434";
    internal const string DefaultApiCompatUrl = "http://localhost:11434/v1";

    /// <summary>
    /// Constructs the <c>ollama</c> command tree.
    /// </summary>
    public static Command Build(Option<string?> targetOption)
    {
        var ollama = new Command("ollama", "Ollama local model diagnostics and helpers.");
        ollama.AddCommand(BuildStatus(targetOption));
        ollama.AddCommand(BuildModels(targetOption));
        ollama.AddCommand(BuildTest(targetOption));
        return ollama;
    }

    private static Command BuildStatus(Option<string?> targetOption)
    {
        var urlOption = new Option<string?>("--url", () => null, $"Ollama server URL (default: {DefaultBaseUrl}).");
        var cmd = new Command("status", "Check Ollama server connectivity and version.");
        cmd.AddOption(urlOption);
        cmd.SetHandler(async context =>
        {
            var url = context.ParseResult.GetValueForOption(urlOption) ?? DefaultBaseUrl;
            context.ExitCode = await ExecuteStatusAsync(url, CancellationToken.None);
        });
        return cmd;
    }

    private static Command BuildModels(Option<string?> targetOption)
    {
        var urlOption = new Option<string?>("--url", () => null, $"Ollama server URL (default: {DefaultBaseUrl}).");
        var cmd = new Command("models", "List models available on the local Ollama instance.");
        cmd.AddOption(urlOption);
        cmd.SetHandler(async context =>
        {
            var url = context.ParseResult.GetValueForOption(urlOption) ?? DefaultBaseUrl;
            context.ExitCode = await ExecuteModelsAsync(url, CancellationToken.None);
        });
        return cmd;
    }

    private static Command BuildTest(Option<string?> targetOption)
    {
        var urlOption = new Option<string?>("--url", () => null, $"Ollama server URL (default: {DefaultBaseUrl}).");
        var modelOption = new Option<string?>("--model", () => null, "Model to test (default: first available model).");
        var promptOption = new Option<string>("--prompt", () => "Respond with the single word: ok.", "Prompt to send.");
        var cmd = new Command("test", "Round-trip a single request through Ollama to confirm end-to-end connectivity.");
        cmd.AddOption(urlOption);
        cmd.AddOption(modelOption);
        cmd.AddOption(promptOption);
        cmd.SetHandler(async context =>
        {
            var url = context.ParseResult.GetValueForOption(urlOption) ?? DefaultBaseUrl;
            var model = context.ParseResult.GetValueForOption(modelOption);
            var prompt = context.ParseResult.GetValueForOption(promptOption) ?? "Respond with the single word: ok.";
            context.ExitCode = await ExecuteTestAsync(url, model, prompt, CancellationToken.None);
        });
        return cmd;
    }

    internal static async Task<int> ExecuteStatusAsync(string baseUrl, CancellationToken ct)
    {
        using var http = CreateHttpClient();
        try
        {
            var response = await http.GetAsync(baseUrl.TrimEnd('/'), ct);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                AnsiConsole.MarkupLine($"[green]✓[/] Ollama is running at [green]{Markup.Escape(baseUrl)}[/]");
                if (!string.IsNullOrWhiteSpace(body))
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape(body.Trim())}[/]");
                return 0;
            }

            AnsiConsole.MarkupLine($"[red]✗[/] Ollama returned HTTP {(int)response.StatusCode} at {Markup.Escape(baseUrl)}");
            return 2;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Cannot reach Ollama at {Markup.Escape(baseUrl)}: {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[dim]Is Ollama running? Start with: ollama serve[/]");
            return 1;
        }
    }

    internal static async Task<int> ExecuteModelsAsync(string baseUrl, CancellationToken ct)
    {
        using var http = CreateHttpClient();
        OllamaTagsResponse? tags;
        try
        {
            tags = await http.GetFromJsonAsync<OllamaTagsResponse>(
                $"{baseUrl.TrimEnd('/')}/api/tags", JsonOptions, ct);
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Cannot reach Ollama at {Markup.Escape(baseUrl)}:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        var models = tags?.Models ?? [];
        if (models.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No models pulled.[/] Run [green]ollama pull <model>[/] to download one.");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Name")
            .AddColumn("Size")
            .AddColumn("Modified")
            .AddColumn("Family")
            .AddColumn("Parameters");

        foreach (var m in models.OrderBy(m => m.Name))
        {
            table.AddRow(
                Markup.Escape(m.Name ?? "—"),
                FormatSize(m.Size),
                m.ModifiedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—",
                Markup.Escape(m.Details?.Family ?? "—"),
                Markup.Escape(m.Details?.ParameterSize ?? "—"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]{models.Count} model(s) at {Markup.Escape(baseUrl)}[/]");
        return 0;
    }

    internal static async Task<int> ExecuteTestAsync(string baseUrl, string? modelId, string prompt, CancellationToken ct)
    {
        using var http = CreateHttpClient();

        // If no model specified, pick the first available
        if (string.IsNullOrWhiteSpace(modelId))
        {
            OllamaTagsResponse? tags;
            try
            {
                tags = await http.GetFromJsonAsync<OllamaTagsResponse>(
                    $"{baseUrl.TrimEnd('/')}/api/tags", JsonOptions, ct);
            }
            catch (HttpRequestException ex)
            {
                AnsiConsole.MarkupLine($"[red]Cannot reach Ollama:[/] {Markup.Escape(ex.Message)}");
                return 1;
            }

            modelId = tags?.Models?.FirstOrDefault()?.Name;
            if (string.IsNullOrWhiteSpace(modelId))
            {
                AnsiConsole.MarkupLine("[red]No models available.[/] Pull one first: [green]ollama pull llama3.2[/]");
                return 1;
            }
        }

        AnsiConsole.MarkupLine($"[dim]→ {Markup.Escape(baseUrl)} | model: {Markup.Escape(modelId)}[/]");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var request = new OllamaChatRequest
            {
                Model = modelId,
                Messages = [new OllamaChatMessage { Role = "user", Content = prompt }],
                Stream = false
            };

            var response = await http.PostAsJsonAsync(
                $"{baseUrl.TrimEnd('/')}/api/chat", request, JsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                AnsiConsole.MarkupLine($"[red]Ollama returned HTTP {(int)response.StatusCode}:[/] {Markup.Escape(errorBody)}");
                return 2;
            }

            var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(JsonOptions, ct);
            sw.Stop();

            var text = result?.Message?.Content?.Trim() ?? "";
            AnsiConsole.MarkupLine($"[green]✓[/] Round-trip succeeded in {sw.ElapsedMilliseconds} ms.");
            if (!string.IsNullOrWhiteSpace(text))
                AnsiConsole.MarkupLine($"[dim]Reply:[/] {Markup.Escape(text)}");

            return 0;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Request failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    /// <summary>
    /// Wizard step that queries Ollama for available models and lets the user pick one
    /// as the default model for the provider configuration.
    /// </summary>
    internal sealed class OllamaPickModelStep : IWizardStep
    {
        public string Name => "ollama-pick-model";

        public async Task<StepResult> ExecuteAsync(WizardContext context, CancellationToken cancellationToken)
        {
            var baseUrl = context.TryGet<string>("ollamaBaseUrl", out var url)
                ? url
                : DefaultBaseUrl;

            using var http = CreateHttpClient();
            OllamaTagsResponse? tags;
            try
            {
                tags = await http.GetFromJsonAsync<OllamaTagsResponse>(
                    $"{baseUrl.TrimEnd('/')}/api/tags", JsonOptions, cancellationToken);
            }
            catch (HttpRequestException)
            {
                AnsiConsole.MarkupLine("[yellow]Could not reach Ollama to discover models.[/]");
                AnsiConsole.MarkupLine("[dim]You can set the default model later in config.json.[/]");
                return StepResult.Continue();
            }

            var models = tags?.Models ?? [];
            if (models.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No models pulled on Ollama.[/] Run [green]ollama pull <model>[/] after setup.");
                return StepResult.Continue();
            }

            var modelNames = models.Select(m => m.Name ?? "unknown").OrderBy(n => n).ToList();
            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select a default model:")
                    .PageSize(15)
                    .AddChoices(modelNames));

            AnsiConsole.MarkupLine($"Default model: [green]{Markup.Escape(selected)}[/]\n");
            context.Set("defaultModel", selected);
            return StepResult.Continue();
        }
    }

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(30) };

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:0.#} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:0.#} MB",
            _ => $"{bytes / 1024.0:0.#} KB"
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // --- Ollama API DTOs ---

    internal sealed class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaModel> Models { get; set; } = [];
    }

    internal sealed class OllamaModel
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("modified_at")]
        public DateTime? ModifiedAt { get; set; }

        [JsonPropertyName("details")]
        public OllamaModelDetails? Details { get; set; }
    }

    internal sealed class OllamaModelDetails
    {
        [JsonPropertyName("family")]
        public string? Family { get; set; }

        [JsonPropertyName("parameter_size")]
        public string? ParameterSize { get; set; }
    }

    internal sealed class OllamaChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("messages")]
        public List<OllamaChatMessage> Messages { get; set; } = [];

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }
    }

    internal sealed class OllamaChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";
    }

    internal sealed class OllamaChatResponse
    {
        [JsonPropertyName("message")]
        public OllamaChatMessage? Message { get; set; }
    }
}
