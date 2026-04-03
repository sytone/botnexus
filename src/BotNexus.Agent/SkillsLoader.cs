using System.Text;
using System.Text.RegularExpressions;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BotNexus.Agent;

/// <summary>
/// Loads skill definitions from SKILL.md files in global and per-agent skill directories.
/// Supports YAML frontmatter for metadata and markdown content for instructions.
/// </summary>
public sealed class SkillsLoader : ISkillsLoader
{
    private static readonly Regex FrontmatterRegex = new(
        @"^---\s*\n(.*?)\n---\s*\n(.*)$",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly string _basePath;
    private readonly ILogger<SkillsLoader> _logger;
    private readonly BotNexusConfig _config;
    private readonly IDeserializer _yamlDeserializer;

    public SkillsLoader(
        string basePath,
        IOptions<BotNexusConfig> config,
        ILogger<SkillsLoader> logger)
    {
        _basePath = basePath;
        _config = config.Value;
        _logger = logger;
        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Skill>> LoadSkillsAsync(string agentName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var globalSkillsDir = Path.Combine(_basePath, "skills");
        var agentSkillsDir = Path.Combine(_basePath, "agents", agentName, "skills");

        var globalSkills = await LoadSkillsFromDirectoryAsync(
            globalSkillsDir,
            SkillScope.Global,
            cancellationToken).ConfigureAwait(false);

        var agentSkills = await LoadSkillsFromDirectoryAsync(
            agentSkillsDir,
            SkillScope.Agent,
            cancellationToken).ConfigureAwait(false);

        var merged = MergeSkills(globalSkills, agentSkills);
        var filtered = ApplyDisabledSkills(merged, agentName);

        _logger.LogDebug(
            "Loaded {Count} skills for agent {AgentName} ({Global} global, {Agent} agent-specific, {Disabled} disabled)",
            filtered.Count,
            agentName,
            globalSkills.Count,
            agentSkills.Count,
            merged.Count - filtered.Count);

        return filtered;
    }

    private async Task<List<Skill>> LoadSkillsFromDirectoryAsync(
        string directory,
        SkillScope scope,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            _logger.LogDebug("Skills directory does not exist: {Directory}", directory);
            return [];
        }

        var skills = new List<Skill>();
        var skillDirs = Directory.GetDirectories(directory);

        foreach (var skillDir in skillDirs)
        {
            var skillName = Path.GetFileName(skillDir);
            var skillFile = Path.Combine(skillDir, "SKILL.md");

            if (!File.Exists(skillFile))
            {
                _logger.LogWarning("Skill directory {SkillName} missing SKILL.md file", skillName);
                continue;
            }

            try
            {
                var content = await File.ReadAllTextAsync(skillFile, cancellationToken).ConfigureAwait(false);
                var skill = ParseSkillFile(skillName, content, skillFile, scope);
                skills.Add(skill);
                _logger.LogDebug("Loaded {Scope} skill: {SkillName}", scope, skillName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load skill {SkillName} from {SkillFile}", skillName, skillFile);
            }
        }

        return skills;
    }

    private Skill ParseSkillFile(string skillName, string content, string sourcePath, SkillScope scope)
    {
        var match = FrontmatterRegex.Match(content);
        
        if (!match.Success)
        {
            return new Skill(
                Name: skillName,
                Description: $"Skill: {skillName}",
                Content: content.Trim(),
                SourcePath: sourcePath,
                Scope: scope);
        }

        var frontmatter = match.Groups[1].Value;
        var body = match.Groups[2].Value.Trim();

        var metadata = _yamlDeserializer.Deserialize<SkillMetadata>(frontmatter) ?? new SkillMetadata();

        return new Skill(
            Name: skillName,
            Description: metadata.Description ?? $"Skill: {skillName}",
            Content: body,
            SourcePath: sourcePath,
            Scope: scope,
            Version: metadata.Version,
            AlwaysLoad: metadata.Always);
    }

    private static List<Skill> MergeSkills(List<Skill> globalSkills, List<Skill> agentSkills)
    {
        var merged = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in globalSkills)
            merged[skill.Name] = skill;

        foreach (var skill in agentSkills)
            merged[skill.Name] = skill;

        return [.. merged.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private List<Skill> ApplyDisabledSkills(List<Skill> skills, string agentName)
    {
        var agentConfig = _config.Agents.Named.GetValueOrDefault(agentName);
        var disabledPatterns = agentConfig?.DisabledSkills ?? [];

        if (disabledPatterns.Count == 0)
            return skills;

        var filtered = new List<Skill>();
        foreach (var skill in skills)
        {
            if (IsSkillDisabled(skill.Name, disabledPatterns))
            {
                _logger.LogDebug("Skill {SkillName} disabled by configuration", skill.Name);
                continue;
            }
            filtered.Add(skill);
        }

        return filtered;
    }

    private static bool IsSkillDisabled(string skillName, List<string> disabledPatterns)
    {
        foreach (var pattern in disabledPatterns)
        {
            if (MatchesPattern(skillName, pattern))
                return true;
        }
        return false;
    }

    private static bool MatchesPattern(string skillName, string pattern)
    {
        if (string.Equals(skillName, pattern, StringComparison.OrdinalIgnoreCase))
            return true;

        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return Regex.IsMatch(skillName, regexPattern, RegexOptions.IgnoreCase);
        }

        return false;
    }

    private sealed class SkillMetadata
    {
        public string? Description { get; set; }
        public string? Version { get; set; }
        public bool Always { get; set; }
    }
}
