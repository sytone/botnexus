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
    public IReadOnlyList<PromptTemplateDescriptor> ListTemplates(AgentId agentId, int limit)
    {
        if (limit <= 0)
            return [];

        return DiscoverTemplates(agentId).Values
            .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(ToDescriptor)
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListTemplateNames(AgentId agentId)
        => ListTemplates(agentId, int.MaxValue).Select(template => template.Name).ToList();

    /// <inheritdoc />
    public PromptTemplateRenderResult Render(
        AgentId agentId,
        string templateName,
        IReadOnlyDictionary<string, string?>? parameters)
    {
        if (string.IsNullOrWhiteSpace(templateName))
            return PromptTemplateRenderResult.Failure("Template name is required.");

        if (TryResolveFileTemplate(agentId, templateName, out var fileTemplate, out var malformed))
            return RenderResolved(fileTemplate, parameters);

        if (malformed)
            return PromptTemplateRenderResult.Failure($"Prompt template '{templateName}' is malformed.");

        var templates = LoadOptionTemplates();
        if (!templates.TryGetValue(templateName, out var template))
            return PromptTemplateRenderResult.Failure($"Prompt template '{templateName}' was not found.");

        return RenderResolved(template, parameters);
    }

    /// <inheritdoc />
    public bool TryRender(
        AgentId agentId,
        string templateName,
        IReadOnlyDictionary<string, string?>? parameters,
        out string renderedPrompt,
        out string? error)
    {
        var result = Render(agentId, templateName, parameters);
        renderedPrompt = result.RenderedPrompt;
        error = result.Succeeded ? null : result.Error;
        return result.Succeeded;
    }

    private static PromptTemplateRenderResult RenderResolved(
        ResolvedPromptTemplate template,
        IReadOnlyDictionary<string, string?>? parameters)
    {
        if (PromptTemplateRenderer.TryRender(
            template.Prompt, parameters, template.Defaults, template.RequiredParameters,
            out var renderedPrompt, out _))
        {
            return PromptTemplateRenderResult.Success(renderedPrompt);
        }

        var supplied = new Dictionary<string, string?>(template.Defaults, StringComparer.OrdinalIgnoreCase);
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
                supplied[name] = value;
        }

        var required = PromptTemplateRenderer.GetRequiredParameters(template.Prompt)
            .Concat(template.RequiredParameters)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !supplied.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return PromptTemplateRenderResult.MissingRequired(required);
    }

    private static PromptTemplateDescriptor ToDescriptor(ResolvedPromptTemplate template)
    {
        var names = PromptTemplateRenderer.GetRequiredParameters(template.Prompt)
            .Concat(template.Defaults.Keys)
            .Concat(template.RequiredParameters)
            .Concat(template.ParameterMetadata.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                template.ParameterMetadata.TryGetValue(name, out var metadata);
                template.Defaults.TryGetValue(name, out var defaultValue);
                return new PromptTemplateParameterDescriptor(
                    name, metadata?.Description, defaultValue,
                    template.RequiredParameters.Contains(name));
            })
            .ToList();
        return new PromptTemplateDescriptor(
            template.Name, template.Description, template.Source, template.ShadowedSources, names);
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
                    if (templates.TryGetValue(parsed.Name, out var shadowed))
                        parsed = parsed with { ShadowedSources = [shadowed.Source, .. shadowed.ShadowedSources] };
                    templates[parsed.Name] = parsed;
                }
                catch
                {
                    // A malformed higher-precedence file is a fail-closed tombstone for the same
                    // effective filename. Listing must not expose a lower template that Render rejects.
                    templates.Remove(GetTemplateStem(templatePath));
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

            var parameterMetadata = configuredTemplate.Parameters?.ToDictionary(
                pair => pair.Key,
                pair => new FrontMatterParameterMetadata(pair.Value.Description, pair.Value.Default, pair.Value.Required),
                StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, FrontMatterParameterMetadata>(StringComparer.OrdinalIgnoreCase);
            templates[name] = new ResolvedPromptTemplate(
                name, configuredTemplate.Description, configuredTemplate.Prompt, defaults, required,
                parameterMetadata, PromptTemplateSource.Configured, []);
        }

        return templates;
    }

    private bool TryResolveFileTemplate(
        AgentId agentId,
        string templateName,
        out ResolvedPromptTemplate template,
        out bool malformed)
    {
        foreach (var (directory, source) in ResolveTemplateDirectories(agentId, highestFirst: true))
        {
            if (!TryFindTemplatePath(directory, templateName, out var templatePath))
                continue;

            try
            {
                template = ParseTemplateFile(templatePath, source);
                malformed = false;
                return true;
            }
            catch
            {
                template = default!;
                malformed = true;
                return false;
            }
        }

        template = default!;
        malformed = false;
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

    private IReadOnlyList<(string Directory, PromptTemplateSource Source)> ResolveTemplateDirectories(AgentId agentId, bool highestFirst)
    {
        var homePath = ResolveBotNexusHomePath();
        var ordered = new List<(string Directory, PromptTemplateSource Source)>
        {
            (_fileSystem.Path.Combine(homePath, "prompts"), PromptTemplateSource.Shared),
            (_fileSystem.Path.Combine(homePath, "agents", agentId.Value, "prompts"), PromptTemplateSource.Agent)
        };

        if (_workspaceManager is not null)
        {
            var workspacePath = _workspaceManager.GetWorkspacePath(agentId.Value);
            ordered.Add((_fileSystem.Path.Combine(workspacePath, "prompts"), PromptTemplateSource.Workspace));
        }

        if (highestFirst)
            ordered.Reverse();

        return ordered;
    }

    private ResolvedPromptTemplate ParseTemplateFile(string templatePath, PromptTemplateSource source)
    {
        if (templatePath.EndsWith(".prompt.md", StringComparison.OrdinalIgnoreCase))
            return ParseMarkdownTemplateFile(templatePath, source);

        if (templatePath.EndsWith(".prompt.json", StringComparison.OrdinalIgnoreCase))
            return ParseJsonTemplateFile(templatePath, source);

        throw new InvalidOperationException($"Template file '{templatePath}' has unsupported extension.");
    }

    private ResolvedPromptTemplate ParseJsonTemplateFile(string templatePath, PromptTemplateSource source)
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

        var description = ReadString(root, "description");
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
        var parameterMetadata = new Dictionary<string, FrontMatterParameterMetadata>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("parameters", out var parametersElement) && parametersElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var parameter in parametersElement.EnumerateObject())
            {
                if (parameter.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var defaultValue = ReadString(parameter.Value, "default");
                if (defaultValue is not null)
                    defaults[parameter.Name] = defaultValue;

                var isRequired = ReadBool(parameter.Value, "required");
                if (isRequired)
                    required.Add(parameter.Name);
                parameterMetadata[parameter.Name] = new FrontMatterParameterMetadata(
                    ReadString(parameter.Value, "description"), defaultValue, isRequired);
            }
        }

        return new ResolvedPromptTemplate(
            name, description, prompt, defaults, required, parameterMetadata, source, []);
    }

    private ResolvedPromptTemplate ParseMarkdownTemplateFile(string templatePath, PromptTemplateSource source)
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

        return new ResolvedPromptTemplate(
            name, metadata.Description, body, defaults, required, metadata.Parameters, source, []);
    }

    private IEnumerable<string> EnumerateTemplatePaths(string directory)
    {
        foreach (var jsonTemplate in _fileSystem.Directory.GetFiles(directory, "*.prompt.json", SearchOption.TopDirectoryOnly))
            yield return jsonTemplate;

        foreach (var markdownTemplate in _fileSystem.Directory.GetFiles(directory, "*.prompt.md", SearchOption.TopDirectoryOnly))
            yield return markdownTemplate;
    }

    private string GetTemplateStem(string templatePath)
        => _fileSystem.Path.GetFileNameWithoutExtension(
            _fileSystem.Path.GetFileNameWithoutExtension(templatePath));

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
            Description: null,
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
                    case "description":
                        metadata = metadata with { Description = ParseYamlScalar(value) };
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
        string? Description,
        string Prompt,
        IReadOnlyDictionary<string, string?> Defaults,
        IReadOnlySet<string> RequiredParameters,
        IReadOnlyDictionary<string, FrontMatterParameterMetadata> ParameterMetadata,
        PromptTemplateSource Source,
        IReadOnlyList<PromptTemplateSource> ShadowedSources);

    private sealed record FrontMatterMetadata(
        string? Name,
        string? Description,
        Dictionary<string, FrontMatterParameterMetadata> Parameters);

    private sealed record FrontMatterParameterMetadata(
        string? Description,
        string? Default,
        bool Required);

}
