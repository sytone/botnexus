using System.Text.RegularExpressions;

namespace BotNexus.Cron.Prompts;

/// <summary>
/// Renders prompt templates using <c>{{parameter}}</c> placeholders.
/// </summary>
public static partial class PromptTemplateRenderer
{
    /// <summary>
    /// Extracts placeholder parameter names from a template body.
    /// </summary>
    /// <param name="template">Template body containing <c>{{name}}</c> placeholders.</param>
    /// <returns>Unique placeholder names, case-insensitive.</returns>
    public static IReadOnlySet<string> GetRequiredParameters(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        HashSet<string> parameters = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in PlaceholderRegex().Matches(template))
        {
            var name = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(name))
                parameters.Add(name);
        }

        return parameters;
    }

    /// <summary>
    /// Attempts to render a template using caller parameters and defaults inferred by template definitions.
    /// </summary>
    public static bool TryRender(
        string template,
        IReadOnlyDictionary<string, string?>? parameters,
        IReadOnlyDictionary<string, string?>? defaults,
        out string renderedPrompt,
        out string? error)
        => TryRender(template, parameters, defaults, requiredParameters: null, out renderedPrompt, out error);

    /// <summary>
    /// Attempts to render a template with explicit required-parameter metadata.
    /// </summary>
    public static bool TryRender(
        string template,
        IReadOnlyDictionary<string, string?>? parameters,
        IReadOnlyDictionary<string, string?>? defaults,
        IReadOnlySet<string>? requiredParameters,
        out string renderedPrompt,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (defaults is not null)
        {
            foreach (var (key, value) in defaults)
                merged[key] = value;
        }

        if (parameters is not null)
        {
            foreach (var (key, value) in parameters)
                merged[key] = value;
        }

        var required = GetRequiredParameters(template).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requiredParameters is not null)
        {
            foreach (var requiredParameter in requiredParameters.Where(static name => !string.IsNullOrWhiteSpace(name)))
                required.Add(requiredParameter);
        }

        var missing = required
            .Where(name => !merged.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count > 0)
        {
            renderedPrompt = string.Empty;
            error = $"Missing required template parameters: {string.Join(", ", missing)}.";
            return false;
        }

        renderedPrompt = PlaceholderRegex().Replace(template, match =>
        {
            var key = match.Groups[1].Value.Trim();
            return merged.TryGetValue(key, out var value) ? value ?? string.Empty : match.Value;
        });
        error = null;
        return true;
    }

    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_.-]+)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();
}
