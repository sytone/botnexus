using BotNexus.Extensions.Skills.Security;
using Microsoft.Extensions.Logging;
using System.IO.Abstractions;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Discovers skills from multiple filesystem paths with priority-based merging.
/// Validates skills per the Agent Skills spec before including them.
/// </summary>
public static class SkillDiscovery
{
    private const string SkillFileName = "SKILL.md";
    private const int MaxDescriptionLength = 1024;
    private const int MaxCompatibilityLength = 500;
    private const long MaxSkillFileBytes = 524_288; // 512 KB

    /// <summary>
    /// Top-level support directories whose contents are surfaced as load-on-demand
    /// linked files. Mirrors the allowed supporting directories in SkillManagerTool.
    /// </summary>
    private static readonly string[] LinkedFileDirs = ["references", "templates", "scripts", "assets"];

    /// <summary>
    /// Discovers and merges skills from plugin, global, agent, and workspace directories.
    /// </summary>
    /// <param name="globalSkillsDir">Optional path to the global skills directory.</param>
    /// <param name="agentSkillsDir">Optional path to the agent-level skills directory.</param>
    /// <param name="workspaceSkillsDir">Optional path to the workspace-level skills directory.</param>
    /// <param name="fileSystem">Optional filesystem abstraction (defaults to real filesystem).</param>
    /// <param name="logger">Optional logger; when supplied, emits warnings for skipped skills.</param>
    /// <param name="trustMode">Trust verification mode applied to every scanned skill.</param>
    /// <param name="pluginSkillsDirs">
    /// Optional skills directories belonging to installed plugins (#2684), typically produced by
    /// <c>PluginSkillRootResolver.Resolve</c>. Scanned FIRST, at the global/shared tier, so a
    /// same-named global, agent or workspace skill still wins. Omitting this argument - or passing
    /// an empty sequence, which is what a machine with no plugins installed yields - leaves the
    /// pre-existing three-scope resolution byte-identical.
    /// </param>
    /// <param name="securityAcknowledgements">
    /// Optional operator-recorded acknowledgements of specific critical scan findings (#3355).
    /// Each entry clears exactly one <c>skill + ruleId + relative file</c> triple; any critical
    /// finding NOT covered by an entry still skips the skill. Null or empty leaves the previous
    /// "any critical finding blocks the skill" behaviour unchanged.
    /// </param>
    public static IReadOnlyList<SkillDefinition> Discover(
        string? globalSkillsDir,
        string? agentSkillsDir,
        string? workspaceSkillsDir,
        IFileSystem? fileSystem = null,
        ILogger? logger = null,
        SkillTrustMode trustMode = SkillTrustMode.Disabled,
        IReadOnlyList<string>? pluginSkillsDirs = null,
        IReadOnlyList<SkillSecurityAcknowledgement>? securityAcknowledgements = null)
    {
        var fs = fileSystem ?? new FileSystem();
        var skills = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);

        // Plugin skills are scanned before the global directory rather than merged into it: they
        // share the global tier but must LOSE a name collision with operator-authored shared
        // content, and "scanned first" is precisely how this dictionary expresses "lower priority".
        if (pluginSkillsDirs is { Count: > 0 })
        {
            foreach (var pluginSkillsDir in pluginSkillsDirs)
            {
                ScanDirectory(skills, pluginSkillsDir, SkillSource.Plugin, fs, logger, trustMode, securityAcknowledgements);
            }
        }

        ScanDirectory(skills, globalSkillsDir, SkillSource.Global, fs, logger, trustMode, securityAcknowledgements);
        ScanDirectory(skills, agentSkillsDir, SkillSource.Agent, fs, logger, trustMode, securityAcknowledgements);
        ScanDirectory(skills, workspaceSkillsDir, SkillSource.Workspace, fs, logger, trustMode, securityAcknowledgements);

        return skills.Values.ToList();
    }

    private static void ScanDirectory(
        Dictionary<string, SkillDefinition> skills,
        string? directory,
        SkillSource source,
        IFileSystem fileSystem,
        ILogger? logger,
        SkillTrustMode trustMode,
        IReadOnlyList<SkillSecurityAcknowledgement>? securityAcknowledgements)
    {
        if (string.IsNullOrWhiteSpace(directory) || !fileSystem.Directory.Exists(directory))
            return;

        foreach (var skillDir in fileSystem.Directory.GetDirectories(directory))
        {
            var skillMdPath = Path.Combine(skillDir, SkillFileName);
            if (!fileSystem.File.Exists(skillMdPath))
                continue;

            var dirName = Path.GetFileName(skillDir);
            try
            {
                var skillFileInfo = fileSystem.FileInfo.New(skillMdPath);
                if (skillFileInfo.Length > MaxSkillFileBytes)
                {
                    logger?.LogWarning(
                        "Skill at '{SkillDir}' skipped: SKILL.md exceeds the {MaxBytes} byte size limit.",
                        skillDir, MaxSkillFileBytes);
                    continue;
                }

                var content = fileSystem.File.ReadAllText(skillMdPath);
                var skill = SkillParser.Parse(dirName, content, skillDir, source);

                if (!TryValidate(skill, dirName, skillDir, logger, out var reason))
                {
                    logger?.LogWarning(
                        "Skill at '{SkillDir}' skipped: {Reason}. Check for UTF-8 BOM or malformed YAML frontmatter.",
                        skillDir, reason);
                    continue;
                }

                // Security scan: block skills with critical findings that no operator has
                // acknowledged. #3355 — a bare count gave the operator nothing to act on, so the
                // outstanding findings are named individually by relative path and ruleId.
                var scanSummary = SkillSecurityScanner.ScanDirectory(skillDir, fileSystem: fileSystem);
                var outstanding = FindUnacknowledgedCriticalFindings(
                    scanSummary, skill.Name, skillDir, fileSystem, securityAcknowledgements);

                if (outstanding.Count > 0)
                {
                    logger?.LogWarning(
                        "Skill at '{SkillDir}' skipped: security scan found unacknowledged critical finding(s): {Findings}. " +
                        "Record a scoped acknowledgement (skill + ruleId + file) to load it anyway.",
                        skillDir, string.Join("; ", outstanding));
                    continue;
                }

                // Trust verification: check script integrity against catalog
                if (trustMode != SkillTrustMode.Disabled)
                {
                    var trustResult = SkillTrustVerifier.Verify(SkillPath.CreateRoot(skillDir, fileSystem), fileSystem);
                    if (!trustResult.Trusted)
                    {
                        var violations = string.Join("; ", trustResult.Violations);
                        if (trustMode == SkillTrustMode.Enforce)
                        {
                            logger?.LogWarning(
                                "Skill at '{SkillDir}' skipped: trust verification failed ({Violations}).",
                                skillDir, violations);
                            continue;
                        }

                        // Warn mode: log but allow
                        logger?.LogWarning(
                            "Skill at '{SkillDir}' has trust violations ({Violations}) - loading anyway (trust mode: warn).",
                            skillDir, violations);
                    }
                }

                // Enrich the skill with its bundled support files so they can be surfaced
                // as load-on-demand context without injecting the entire directory.
                skills[skill.Name] = skill with { LinkedFiles = DiscoverLinkedFiles(skillDir, fileSystem) };
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "Skill at '{SkillDir}' skipped: SKILL.md could not be loaded - {Message}.",
                    skillDir, ex.Message);
            }
        }
    }

    /// <summary>
    /// Reduces a scan summary to the critical findings the operator has NOT acknowledged,
    /// formatted as <c>relative/path.mjs:LINE (ruleId)</c> so the log line alone is actionable.
    /// </summary>
    /// <remarks>
    /// Matching is per-FINDING, never per-file and never per-skill: a second, different rule
    /// firing in an already-acknowledged file yields a new entry here and still skips the skill.
    /// That is the whole point of #3355 clause 3 — an acknowledgement records what a human
    /// actually reviewed, so it must not inherit approval for something they never saw.
    /// </remarks>
    private static List<string> FindUnacknowledgedCriticalFindings(
        ScanSummary scanSummary,
        string skillName,
        string skillDir,
        IFileSystem fileSystem,
        IReadOnlyList<SkillSecurityAcknowledgement>? acknowledgements)
    {
        var outstanding = new List<string>();

        foreach (var finding in scanSummary.Findings)
        {
            if (finding.Severity != ScanSeverity.Critical)
                continue;

            var relative = Path.GetRelativePath(skillDir, finding.File).Replace('\\', '/');

            var acknowledged = acknowledgements is { Count: > 0 }
                && acknowledgements.Any(ack => SkillSecurityAcknowledgements.IsAcknowledged(
                    ack, skillName, relative, finding.RuleId, fileSystem, finding.File));

            if (!acknowledged)
                outstanding.Add($"{relative}:{finding.Line} ({finding.RuleId})");
        }

        return outstanding;
    }

    /// <summary>
    /// Enumerates support files under the well-known linked-file directories
    /// (references/, templates/, scripts/, assets/) for a single skill. Paths are returned
    /// relative to the skill directory with forward slashes for cross-platform stability.
    /// </summary>
    private static IReadOnlyList<SkillLinkedFile> DiscoverLinkedFiles(string skillDir, IFileSystem fileSystem)
    {
        var linked = new List<SkillLinkedFile>();

        foreach (var group in LinkedFileDirs)
        {
            var groupDir = Path.Combine(skillDir, group);
            if (!fileSystem.Directory.Exists(groupDir))
                continue;

            foreach (var file in fileSystem.Directory.GetFiles(groupDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(skillDir, file).Replace('\\', '/');
                long size = 0;
                try
                {
                    size = fileSystem.FileInfo.New(file).Length;
                }
                catch
                {
                    // Size is best-effort display metadata; a stat failure must not drop the file.
                }

                linked.Add(new SkillLinkedFile
                {
                    RelativePath = relative,
                    Directory = group,
                    SizeBytes = size
                });
            }
        }

        // Deterministic ordering keeps the "Linked files" listing stable across runs.
        return linked
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Returns <c>true</c> when the skill passes all validation rules.
    /// On failure, <paramref name="failureReason"/> is set to a human-readable explanation.
    /// </summary>
    private static bool TryValidate(
        SkillDefinition skill,
        string directoryName,
        string skillDir,
        ILogger? logger,
        out string failureReason)
    {
        if (!SkillParser.IsValidName(skill.Name))
        {
            failureReason = $"name '{skill.Name}' is not a valid skill name (lowercase alphanumeric and hyphens, max 64 chars)";
            return false;
        }

        if (!string.Equals(skill.Name, directoryName, StringComparison.Ordinal))
        {
            failureReason = $"name '{skill.Name}' does not match directory name '{directoryName}'";
            return false;
        }

        if (string.IsNullOrWhiteSpace(skill.Description))
        {
            failureReason = "description field is missing or empty";
            return false;
        }

        if (skill.Description.Length > MaxDescriptionLength)
        {
            failureReason = $"description exceeds the {MaxDescriptionLength}-character limit ({skill.Description.Length} chars)";
            return false;
        }

        // Per spec: compatibility must be 1-500 characters if provided
        if (skill.Compatibility is not null && skill.Compatibility.Length > MaxCompatibilityLength)
        {
            failureReason = $"compatibility field exceeds the {MaxCompatibilityLength}-character limit";
            return false;
        }

        // Skills with disable-model-invocation are excluded from model context
        if (skill.DisableModelInvocation)
        {
            failureReason = "disable-model-invocation is set - skill is excluded from context injection";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }
}
