using System.IO.Abstractions;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Agents;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Prompts;

/// <summary>
/// Resolves prompt templates from file-backed template directories with options-based templates as fallback.
/// </summary>
public sealed class CronOptionsPromptTemplateResolver(
    IOptionsMonitor<CronOptions> optionsMonitor,
    IAgentWorkspaceManager? workspaceManager = null,
    IFileSystem? fileSystem = null) : IPromptTemplateResolver
{
    private readonly IOptionsMonitor<CronOptions> _optionsMonitor = optionsMonitor;
    private readonly IAgentWorkspaceManager? _workspaceManager = workspaceManager;
    private readonly IFileSystem _fileSystem = fileSystem ?? new FileSystem();

    /// <inheritdoc />
    public IReadOnlyList<string> ListTemplateNames(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return [];

        var templates = DiscoverTemplates(agentId);
        return templates.Keys
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    public bool TryRender(
        string agentId,
        string templateName,
        IReadOnlyDictionary<string, string?>? parameters,
        out string renderedPrompt,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            renderedPrompt = string.Empty;
            error = "Agent id is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(templateName))
        {
            renderedPrompt = string.Empty;
            error = "Template name is required.";
            return false;
        }

        if (TryResolveFileTemplate(agentId, templateName, out var fileTemplate, out error))
        {
            return PromptTemplateRenderer.TryRender(
                fileTemplate.Prompt,
                parameters,
                fileTemplate.Defaults,
                fileTemplate.RequiredParameters,
                out renderedPrompt,
                out error);
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            renderedPrompt = string.Empty;
            return false;
        }

        var templates = LoadOptionTemplates();
        if (!templates.TryGetValue(templateName, out var template) || string.IsNullOrWhiteSpace(template.Prompt))
        {
            renderedPrompt = string.Empty;
            error = $"Prompt template '{templateName}' was not found.";
            return false;
        }

        return PromptTemplateRenderer.TryRender(
            template.Prompt,
            parameters,
            template.Defaults,
            template.RequiredParameters,
            out renderedPrompt,
            out error);
    }

    private IReadOnlyDictionary<string, ResolvedPromptTemplate> DiscoverTemplates(string agentId)
    {
        var templates = LoadOptionTemplates();
        foreach (var (directory, source) in ResolveTemplateDirectories(agentId, highestFirst: false))
        {
            if (!_fileSystem.Directory.Exists(directory))
                continue;

            foreach (var templatePath in _fileSystem.Directory.GetFiles(directory, "*.prompt.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var parsed = ParseTemplateFile(templatePath, source);
                    templates[parsed.Name] = parsed;
                }
                catch
                {
                    // Ignore malformed files during listing/discovery; render path surfaces deterministic errors.
                }
            }
        }

        return templates;
    }

    private Dictionary<string, ResolvedPromptTemplate> LoadOptionTemplates()
    {
        var templates = new Dictionary<string, ResolvedPromptTemplate>(StringComparer.OrdinalIgnoreCase);
        var configuredTemplates = _optionsMonitor.CurrentValue?.PromptTemplates;
        if (configuredTemplates is null || configuredTemplates.Count == 0)
            return templates;

        foreach (var (name, configuredTemplate) in configuredTemplates)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(configuredTemplate.Prompt))
                continue;

            var defaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (configuredTemplate.Defaults is not null)
            {
                foreach (var (key, value) in configuredTemplate.Defaults)
                    defaults[key] = value;
            }

            if (configuredTemplate.Parameters is not null)
            {
                foreach (var (key, value) in configuredTemplate.Parameters)
                {
                    if (value.Default is not null)
                        defaults[key] = value.Default;
                }
            }

            var required = configuredTemplate.Parameters?
                .Where(pair => pair.Value.Required)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            templates[name] = new ResolvedPromptTemplate(name, configuredTemplate.Prompt, defaults, required, TemplateSource.Options);
        }

        return templates;
    }

    private bool TryResolveFileTemplate(
        string agentId,
        string templateName,
        out ResolvedPromptTemplate template,
        out string? error)
    {
        foreach (var (directory, source) in ResolveTemplateDirectories(agentId, highestFirst: true))
        {
            if (!TryFindTemplatePath(directory, templateName, out var templatePath))
                continue;

            try
            {
                template = ParseTemplateFile(templatePath, source);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                template = default!;
                error = ex.Message;
                return false;
            }
        }

        template = default!;
        error = null;
        return false;
    }

    private bool TryFindTemplatePath(string directory, string templateName, out string templatePath)
    {
        var exact = _fileSystem.Path.Combine(directory, $"{templateName}.prompt.json");
        if (_fileSystem.File.Exists(exact))
        {
            templatePath = exact;
            return true;
        }

        if (!_fileSystem.Directory.Exists(directory))
        {
            templatePath = string.Empty;
            return false;
        }

        var normalizedName = templateName.Trim();
        foreach (var candidate in _fileSystem.Directory.GetFiles(directory, "*.prompt.json", SearchOption.TopDirectoryOnly))
        {
            var stem = _fileSystem.Path.GetFileNameWithoutExtension(_fileSystem.Path.GetFileNameWithoutExtension(candidate));
            if (string.Equals(stem, normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                templatePath = candidate;
                return true;
            }
        }

        templatePath = string.Empty;
        return false;
    }

    private IReadOnlyList<(string Directory, TemplateSource Source)> ResolveTemplateDirectories(string agentId, bool highestFirst)
    {
        var homePath = ResolveBotNexusHomePath();
        var ordered = new List<(string Directory, TemplateSource Source)>
        {
            (_fileSystem.Path.Combine(homePath, "prompts"), TemplateSource.Shared),
            (_fileSystem.Path.Combine(homePath, "agents", agentId, "prompts"), TemplateSource.Agent)
        };

        if (_workspaceManager is not null)
        {
            var workspacePath = _workspaceManager.GetWorkspacePath(agentId);
            ordered.Add((_fileSystem.Path.Combine(workspacePath, "prompts"), TemplateSource.Workspace));
        }

        if (highestFirst)
            ordered.Reverse();

        return ordered;
    }

    private ResolvedPromptTemplate ParseTemplateFile(string templatePath, TemplateSource source)
    {
        var raw = _fileSystem.File.ReadAllText(templatePath);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Template file '{templatePath}' must be a JSON object.");

        var fallbackName = _fileSystem.Path.GetFileNameWithoutExtension(
            _fileSystem.Path.GetFileNameWithoutExtension(templatePath));
        var name = ReadString(root, "name") ?? fallbackName;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException($"Template file '{templatePath}' has invalid name.");

        var prompt = ReadString(root, "prompt") ?? ReadString(root, "template");
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException($"Template file '{templatePath}' has no prompt body.");

        var defaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("parameters", out var parametersElement) && parametersElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var parameter in parametersElement.EnumerateObject())
            {
                if (parameter.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var defaultValue = ReadString(parameter.Value, "default");
                if (defaultValue is not null)
                    defaults[parameter.Name] = defaultValue;

                if (ReadBool(parameter.Value, "required"))
                    required.Add(parameter.Name);
            }
        }

        return new ResolvedPromptTemplate(name, prompt, defaults, required, source);
    }

    private string ResolveBotNexusHomePath()
    {
        var configured = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
            return _fileSystem.Path.GetFullPath(configured);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            userProfile = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;

        return _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(userProfile, ".botnexus"));
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => property.GetString(),
            _ => property.ToString()
        };
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => false
        };
    }

    private sealed record ResolvedPromptTemplate(
        string Name,
        string Prompt,
        IReadOnlyDictionary<string, string?> Defaults,
        IReadOnlySet<string> RequiredParameters,
        TemplateSource Source);

    private enum TemplateSource
    {
        Options = 0,
        Shared = 1,
        Agent = 2,
        Workspace = 3
    }
}
