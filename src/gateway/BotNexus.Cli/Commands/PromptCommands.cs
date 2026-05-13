using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Commands;

internal sealed partial class PromptCommands
{
    public Command Build(Option<bool> verboseOption)
    {
        _ = verboseOption;
        var prompt = new Command("prompt", "Manage prompt templates.");
        prompt.AddCommand(new Command("list", "List prompt templates."));
        prompt.AddCommand(new Command("render", "Render a prompt template."));
        prompt.AddCommand(new Command("run", "Render and run a prompt template."));
        return prompt;
    }

    public static bool TryParseParameters(
        IReadOnlyList<string> rawParameters,
        out Dictionary<string, string> parameters,
        out string? error)
    {
        parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;

        foreach (var raw in rawParameters)
        {
            var separatorIndex = raw.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == raw.Length - 1)
            {
                error = "Invalid parameter format. Use --param key=value.";
                parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return false;
            }

            var key = raw[..separatorIndex].Trim();
            var value = raw[(separatorIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                error = "Invalid parameter format. Use --param key=value.";
                parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
            return 1;

        if (!TryParseParameters(rawParameters, out var parameters, out _))
            return 1;

        var configJson = await File.ReadAllTextAsync(configPath, cancellationToken);
        var config = JsonSerializer.Deserialize<PlatformConfig>(configJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (config?.PromptTemplates is null
            || !config.PromptTemplates.TryGetValue(templateName, out var template)
            || string.IsNullOrWhiteSpace(template.Prompt))
            return 1;

        var defaults = template.Defaults?.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var merged = new Dictionary<string, string>(defaults, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in parameters)
            merged[pair.Key] = pair.Value;

        var required = PlaceholderRegex().Matches(template.Prompt)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (required.Any(name => !merged.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value)))
            return 1;

        var rendered = PlaceholderRegex().Replace(template.Prompt, match =>
        {
            var key = match.Groups[1].Value.Trim();
            return merged.TryGetValue(key, out var value) ? value : match.Value;
        });

        Console.WriteLine(rendered);
        return 0;
    }

    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_.-]+)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();
}
