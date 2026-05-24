using System.IO.Abstractions;
using System.Text.Json;
using BotNexus.Domain.Primitives;
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
    public IReadOnlyList<string> ListTemplateNames(AgentId agentId)
    {
        var templates = DiscoverTemplates(agentId);
        return templates.Keys
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    public bool TryRender(
        AgentId agentId,
        string templateName,
        IReadOnlyDictionary<string, string?>? parameters,
        out string renderedPrompt,
        out string? error)
    {
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

    private IReadOnlyDictionary<string, ResolvedPromptTemplate> DiscoverTemplates(AgentId agentId)
    {
        var templates = LoadOptionTemplates();
        foreach (var (directory, source) in ResolveTemplateDirectories(agentId, highestFirst: false))
        {
            if (!_fileSystem.Directory.Exists(directory))
                continue;

            foreach (var templatePath in EnumerateTemplatePaths(directory))
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
        AgentId agentId,
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
        var markdownExactPath = _fileSystem.Path.Combine(directory, $"{templateName}.prompt.md");
        if (_fileSystem.File.Exists(markdownExactPath))
        {
            templatePath = markdownExactPath;
            return true;
        }

        var jsonExactPath = _fileSystem.Path.Combine(directory, $"{templateName}.prompt.json");
        if (_fileSystem.File.Exists(jsonExactPath))
        {
            templatePath = jsonExactPath;
            return true;
        }

        if (!_fileSystem.Directory.Exists(directory))
        {
            templatePath = string.Empty;
            return false;
        }

        var normalizedName = templateName.Trim();
        string? selectedCandidate = null;
        var selectedPriority = -1;
        foreach (var candidate in EnumerateTemplatePaths(directory))
        {
            var stem = _fileSystem.Path.GetFileNameWithoutExtension(_fileSystem.Path.GetFileNameWithoutExtension(candidate));
            if (!string.Equals(stem, normalizedName, StringComparison.OrdinalIgnoreCase))
                continue;

            var priority = GetTemplateExtensionPriority(candidate);
            if (priority > selectedPriority)
            {
                selectedPriority = priority;
                selectedCandidate = candidate;
            }
        }

        if (selectedCandidate is not null)
        {
            templatePath = selectedCandidate;
            return true;
        }

        templatePath = string.Empty;
        return false;
    }

    private IReadOnlyList<(string Directory, TemplateSource Source)> ResolveTemplateDirectories(AgentId agentId, bool highestFirst)
    {
        var homePath = ResolveBotNexusHomePath();
        var ordered = new List<(string Directory, TemplateSource Source)>
        {
            (_fileSystem.Path.Combine(homePath, "prompts"), TemplateSource.Shared),
            (_fileSystem.Path.Combine(homePath, "agents", agentId.Value, "prompts"), TemplateSource.Agent)
        };

        if (_workspaceManager is not null)
        {
            var workspacePath = _workspaceManager.GetWorkspacePath(agentId.Value);
            ordered.Add((_fileSystem.Path.Combine(workspacePath, "prompts"), TemplateSource.Workspace));
        }

        if (highestFirst)
            ordered.Reverse();

        return ordered;
    }

    private ResolvedPromptTemplate ParseTemplateFile(string templatePath, TemplateSource source)
    {
        if (templatePath.EndsWith(".prompt.md", StringComparison.OrdinalIgnoreCase))
            return ParseMarkdownTemplateFile(templatePath, source);

        if (templatePath.EndsWith(".prompt.json", StringComparison.OrdinalIgnoreCase))
            return ParseJsonTemplateFile(templatePath, source);

        throw new InvalidOperationException($"Template file '{templatePath}' has unsupported extension.");
    }

    private ResolvedPromptTemplate ParseJsonTemplateFile(string templatePath, TemplateSource source)
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
        if (root.TryGetProperty("defaults", out var defaultsElement) && defaultsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var defaultEntry in defaultsElement.EnumerateObject())
                defaults[defaultEntry.Name] = ReadString(defaultsElement, defaultEntry.Name);
        }

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

    private ResolvedPromptTemplate ParseMarkdownTemplateFile(string templatePath, TemplateSource source)
    {
        var raw = _fileSystem.File.ReadAllText(templatePath);
        if (!TrySplitFrontMatter(raw, out var frontMatterText, out var body, out var splitError))
            throw new InvalidOperationException($"Template file '{templatePath}' {splitError}");

        var metadata = ParseFrontMatterMetadata(frontMatterText);
        var fallbackName = _fileSystem.Path.GetFileNameWithoutExtension(
            _fileSystem.Path.GetFileNameWithoutExtension(templatePath));
        var name = metadata.Name ?? fallbackName;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException($"Template file '{templatePath}' has invalid name.");

        body = body.TrimStart('\r', '\n').TrimEnd();
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException($"Template file '{templatePath}' has no prompt body.");

        var defaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (parameterName, parameter) in metadata.Parameters)
        {
            if (parameter.Default is not null)
                defaults[parameterName] = parameter.Default;

            if (parameter.Required)
                required.Add(parameterName);
        }

        return new ResolvedPromptTemplate(name, body, defaults, required, source);
    }

    private IEnumerable<string> EnumerateTemplatePaths(string directory)
    {
        foreach (var jsonTemplate in _fileSystem.Directory.GetFiles(directory, "*.prompt.json", SearchOption.TopDirectoryOnly))
            yield return jsonTemplate;

        foreach (var markdownTemplate in _fileSystem.Directory.GetFiles(directory, "*.prompt.md", SearchOption.TopDirectoryOnly))
            yield return markdownTemplate;
    }

    private static int GetTemplateExtensionPriority(string templatePath)
    {
        if (templatePath.EndsWith(".prompt.md", StringComparison.OrdinalIgnoreCase))
            return 2;

        if (templatePath.EndsWith(".prompt.json", StringComparison.OrdinalIgnoreCase))
            return 1;

        return 0;
    }

    private static bool TrySplitFrontMatter(string content, out string frontMatter, out string body, out string? error)
    {
        frontMatter = string.Empty;
        body = string.Empty;
        error = null;

        var text = content.Length > 0 && content[0] == '\uFEFF'
            ? content[1..]
            : content;

        var (firstLine, firstLineNextIndex) = ReadLine(text, 0);
        if (!string.Equals(firstLine.Trim(), "---", StringComparison.Ordinal))
        {
            error = "must start with YAML front matter delimited by '---'.";
            return false;
        }

        var index = firstLineNextIndex;
        while (index <= text.Length)
        {
            var lineStart = index;
            var (line, nextIndex) = ReadLine(text, index);
            if (string.Equals(line.Trim(), "---", StringComparison.Ordinal))
            {
                frontMatter = text[firstLineNextIndex..lineStart];
                body = nextIndex < text.Length ? text[nextIndex..] : string.Empty;
                return true;
            }

            if (nextIndex == index)
                break;

            index = nextIndex;
        }

        error = "is missing the closing YAML front matter delimiter '---'.";
        return false;
    }

    private static (string Line, int NextIndex) ReadLine(string text, int startIndex)
    {
        if (startIndex >= text.Length)
            return (string.Empty, text.Length);

        var index = startIndex;
        while (index < text.Length && text[index] is not '\r' and not '\n')
            index++;

        var line = text[startIndex..index];
        if (index < text.Length)
        {
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                index += 2;
            else
                index++;
        }

        return (line, index);
    }

    private static FrontMatterMetadata ParseFrontMatterMetadata(string frontMatter)
    {
        var metadata = new FrontMatterMetadata(
            Name: null,
            Parameters: new Dictionary<string, FrontMatterParameterMetadata>(StringComparer.OrdinalIgnoreCase));

        var lines = frontMatter.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inParameters = false;
        string? currentParameter = null;
        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var indent = rawLine.Length - rawLine.TrimStart().Length;
            if (indent == 0)
            {
                currentParameter = null;
                inParameters = false;
                if (!TrySplitYamlPair(trimmed, out var key, out var value, out var hasValue))
                    continue;

                switch (key.ToLowerInvariant())
                {
                    case "name":
                        metadata = metadata with { Name = ParseYamlScalar(value) };
                        break;
                    case "parameters" when !hasValue:
                        inParameters = true;
                        break;
                }

                continue;
            }

            if (!inParameters)
                continue;

            if (indent == 2)
            {
                if (!TrySplitYamlPair(trimmed, out var key, out _, out var hasValue) || hasValue)
                    continue;

                currentParameter = key;
                if (!metadata.Parameters.ContainsKey(currentParameter))
                    metadata.Parameters[currentParameter] = new FrontMatterParameterMetadata(null, null, false);
                continue;
            }

            if (indent >= 4 && currentParameter is not null)
            {
                if (!TrySplitYamlPair(trimmed, out var key, out var value, out _))
                    continue;

                var existing = metadata.Parameters[currentParameter];
                metadata.Parameters[currentParameter] = key.ToLowerInvariant() switch
                {
                    "description" => existing with { Description = ParseYamlScalar(value) },
                    "default" => existing with { Default = ParseYamlScalar(value) },
                    "required" => existing with { Required = bool.TryParse(ParseYamlScalar(value), out var required) && required },
                    _ => existing
                };
            }
        }

        return metadata;
    }

    private static bool TrySplitYamlPair(string line, out string key, out string value, out bool hasValue)
    {
        var separatorIndex = line.IndexOf(':');
        if (separatorIndex <= 0)
        {
            key = string.Empty;
            value = string.Empty;
            hasValue = false;
            return false;
        }

        key = line[..separatorIndex].Trim();
        value = line[(separatorIndex + 1)..].Trim();
        hasValue = value.Length > 0;
        return key.Length > 0;
    }

    private static string? ParseYamlScalar(string value)
    {
        if (value.Length == 0 || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
            return null;

        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
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

    private sealed record FrontMatterMetadata(
        string? Name,
        Dictionary<string, FrontMatterParameterMetadata> Parameters);

    private sealed record FrontMatterParameterMetadata(
        string? Description,
        string? Default,
        bool Required);

    private enum TemplateSource
    {
        Options = 0,
        Shared = 1,
        Agent = 2,
        Workspace = 3
    }
}
