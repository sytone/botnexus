using System.Text.RegularExpressions;
using System.IO.Abstractions;

namespace BotNexus.CodingAgent.Extensions;

/// <summary>
/// Loads markdown skill instructions that are injected into system prompts.
/// Respects <c>.gitignore</c> patterns in skill directories to exclude ignored paths.
/// </summary>
public sealed class SkillsLoader
{
    private const int MaxSkillNameLength = 64;
    private const int MaxSkillDescriptionLength = 1024;
    private static readonly string[] DefaultIgnoredDirectories = ["node_modules", "bin", "obj", ".git", "build", "dist"];
    private readonly IFileSystem _fileSystem;

    public SkillsLoader(IFileSystem? fileSystem = null)
    {
        _fileSystem = fileSystem ?? new FileSystem();
    }

    public IReadOnlyList<string> LoadSkills(string workingDirectory, CodingAgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return [];
        }

        var root = Path.GetFullPath(workingDirectory);
        var skillDocuments = new List<string>();
        var knownSkillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        TryAddFile(Path.Combine(root, "AGENTS.md"), skillDocuments);
        TryAddFile(Path.Combine(root, ".botnexus-agent", "AGENTS.md"), skillDocuments);

        foreach (var skillsDirectory in ResolveSkillDirectories(root, config))
        {
            if (!_fileSystem.Directory.Exists(skillsDirectory))
            {
                continue;
            }

            var ignoreFilter = SkillIgnoreFilter.Create(_fileSystem, skillsDirectory);
            foreach (var skillPath in _fileSystem.Directory.EnumerateFiles(skillsDirectory, "SKILL.md", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (ignoreFilter.IsIgnored(skillPath))
                {
                    continue;
                }

                TryAddSkill(skillPath, skillDocuments, knownSkillNames);
            }
        }

        return skillDocuments;
    }

    private void TryAddFile(string path, ICollection<string> target)
    {
        if (!_fileSystem.File.Exists(path))
        {
            return;
        }

        var content = _fileSystem.File.ReadAllText(path);
        if (!string.IsNullOrWhiteSpace(content))
        {
            target.Add(content);
        }
    }

    private void TryAddSkill(string path, ICollection<string> target, ISet<string> knownSkillNames)
    {
        if (!_fileSystem.File.Exists(path))
        {
            return;
        }

        var content = _fileSystem.File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var parsed = ParseSkillDocument(path, content);
        if (parsed is null)
        {
            return;
        }

        var validationErrors = ValidateSkill(path, parsed);
        if (validationErrors.Count > 0)
        {
            Console.Error.WriteLine($"[warning] Ignoring skill '{parsed.Name}' from '{path}': {string.Join("; ", validationErrors)}");
            return;
        }

        if (!knownSkillNames.Add(parsed.Name))
        {
            Console.Error.WriteLine($"[warning] Duplicate skill name '{parsed.Name}' detected at '{path}'. Keeping first occurrence.");
            return;
        }

        if (parsed.DisableModelInvocation)
        {
            return;
        }

        var normalized = $"""
            ---
            name: {parsed.Name}
            description: {parsed.Description}
            disable-model-invocation: {parsed.DisableModelInvocation.ToString().ToLowerInvariant()}
            ---
            {parsed.Body}
            """;
        target.Add(normalized);
    }

    private static ParsedSkill? ParseSkillDocument(string path, string content)
    {
        var trimmed = content.Trim();
        var defaultName = Path.GetFileName(Path.GetDirectoryName(path) ?? path).ToLowerInvariant();

        if (!trimmed.StartsWith("---", StringComparison.Ordinal))
        {
            return new ParsedSkill(defaultName, null, false, trimmed);
        }

        var lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
        var closingIndex = Array.FindIndex(lines, 1, line => line.Trim().Equals("---", StringComparison.Ordinal));
        if (closingIndex < 0)
        {
            return new ParsedSkill(defaultName, null, false, trimmed);
        }

        var metadata = ParseFrontmatter(lines, closingIndex);
        var name = metadata.TryGetValue("name", out var rawName) && !string.IsNullOrWhiteSpace(rawName)
            ? rawName
            : defaultName;
        var description = metadata.TryGetValue("description", out var rawDescription) ? rawDescription : null;
        var disableModelInvocation = metadata.TryGetValue("disable-model-invocation", out var rawDisable)
            && bool.TryParse(rawDisable, out var parsedDisable)
            && parsedDisable;
        var body = string.Join(Environment.NewLine, lines.Skip(closingIndex + 1)).Trim();

        return new ParsedSkill(name, description, disableModelInvocation, body);
    }

    private static Dictionary<string, string> ParseFrontmatter(string[] lines, int closingIndex)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < closingIndex; index++)
        {
            var line = lines[index];
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"', '\'');
            if (!string.IsNullOrWhiteSpace(key))
            {
                metadata[key] = value;
            }
        }

        return metadata;
    }

    private static bool IsValidSkillName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxSkillNameLength)
        {
            return false;
        }

        if (!name.All(static character => char.IsLower(character) || char.IsDigit(character) || character == '-'))
        {
            return false;
        }

        if (name.StartsWith("-", StringComparison.Ordinal) || name.EndsWith("-", StringComparison.Ordinal))
        {
            return false;
        }

        return !name.Contains("--", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> ValidateSkill(string path, ParsedSkill parsed)
    {
        var errors = new List<string>();
        var parentDirectoryName = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);

        if (!string.Equals(parsed.Name, parentDirectoryName, StringComparison.Ordinal))
        {
            errors.Add($"name \"{parsed.Name}\" must match parent directory \"{parentDirectoryName}\"");
        }

        if (!IsValidSkillName(parsed.Name))
        {
            errors.Add("name must be <= 64 chars, lowercase alphanumeric, and hyphen-safe");
        }

        if (string.IsNullOrWhiteSpace(parsed.Description))
        {
            errors.Add("description is required");
        }
        else if (parsed.Description.Length > MaxSkillDescriptionLength)
        {
            errors.Add($"description exceeds {MaxSkillDescriptionLength} characters");
        }

        return errors;
    }

    private sealed record ParsedSkill(string Name, string? Description, bool DisableModelInvocation, string Body);

    private static IReadOnlyList<string> ResolveSkillDirectories(string root, CodingAgentConfig config)
    {
        var directories = new List<string>
        {
            Path.Combine(root, ".botnexus-agent", "skills")
        };

        if (!string.IsNullOrWhiteSpace(config.SkillsDirectory))
        {
            directories.Add(Path.GetFullPath(config.SkillsDirectory));
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            directories.Add(Path.Combine(userProfile, ".botnexus", "skills"));
        }

        return directories
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed class SkillIgnoreFilter
    {
        private readonly string _basePath;
        private readonly IReadOnlyList<GitIgnoreRule> _rules;

        private SkillIgnoreFilter(string basePath, IReadOnlyList<GitIgnoreRule> rules)
        {
            _basePath = basePath;
            _rules = rules;
        }

        public static SkillIgnoreFilter Create(IFileSystem fileSystem, string basePath)
        {
            var rules = new List<GitIgnoreRule>();
            var gitIgnorePath = Path.Combine(basePath, ".gitignore");
            if (fileSystem.File.Exists(gitIgnorePath))
            {
                foreach (var line in fileSystem.File.ReadAllLines(gitIgnorePath))
                {
                    var parsed = ParseRule(line);
                    if (parsed is not null)
                    {
                        rules.Add(parsed);
                    }
                }
            }

            return new SkillIgnoreFilter(basePath, rules);
        }

        public bool IsIgnored(string fullPath)
        {
            var relative = Path.GetRelativePath(_basePath, fullPath).Replace('\\', '/');
            if (relative.StartsWith("../", StringComparison.Ordinal) || relative.Equals("..", StringComparison.Ordinal))
            {
                return true;
            }

            if (ContainsDefaultIgnoredDirectory(relative))
            {
                return true;
            }

            var ignored = false;
            foreach (var rule in _rules)
            {
                if (rule.Pattern.IsMatch(relative))
                {
                    ignored = !rule.Negated;
                }
            }

            return ignored;
        }

        private static bool ContainsDefaultIgnoredDirectory(string relativePath)
        {
            foreach (var ignoredDirectory in DefaultIgnoredDirectories)
            {
                if (relativePath.Equals(ignoredDirectory, StringComparison.OrdinalIgnoreCase) ||
                    relativePath.StartsWith($"{ignoredDirectory}/", StringComparison.OrdinalIgnoreCase) ||
                    relativePath.Contains($"/{ignoredDirectory}/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private static GitIgnoreRule? ParseRule(string line)
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
        {
            return null;
        }

        var negated = trimmed.StartsWith('!');
        var pattern = negated ? trimmed[1..].Trim() : trimmed;
        if (string.IsNullOrEmpty(pattern))
        {
            return null;
        }

        var anchored = pattern.StartsWith('/');
        if (anchored)
        {
            pattern = pattern[1..];
        }

        var directoryOnly = pattern.EndsWith('/');
        if (directoryOnly)
        {
            pattern = pattern.TrimEnd('/');
        }

        if (string.IsNullOrEmpty(pattern))
        {
            return null;
        }

        var hasSlash = pattern.Contains('/');
        var regex = BuildGitIgnoreRegex(pattern, anchored, hasSlash, directoryOnly);
        return new GitIgnoreRule(regex, negated);
    }

    private static Regex BuildGitIgnoreRegex(string pattern, bool anchored, bool hasSlash, bool directoryOnly)
    {
        var escaped = Regex.Escape(pattern)
            .Replace(@"\*\*", "<<<DOUBLE_STAR>>>", StringComparison.Ordinal)
            .Replace(@"\*", @"[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", @"[^/]", StringComparison.Ordinal)
            .Replace("<<<DOUBLE_STAR>>>", ".*", StringComparison.Ordinal);

        var prefix = anchored ? "^" : @"(^|.*/)";
        var suffix = directoryOnly ? @"(?:/.*)?$" : "$";
        return new Regex($"{prefix}{escaped}{suffix}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    private sealed record GitIgnoreRule(Regex Pattern, bool Negated);
}
