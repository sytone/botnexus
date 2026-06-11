using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Extensions.Skills.Security;
using System.IO.Abstractions;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Agent-facing tool for creating, editing, patching, deleting, and managing supporting files
/// for skills at runtime. Only contributed when <see cref="SkillsConfig.AllowSkillCreation"/>
/// is enabled. Destructive actions additionally require <see cref="SkillsConfig.AllowSkillDeletion"/>.
/// </summary>
public sealed class SkillManagerTool(
    string? agentSkillsDir,
    string? workspaceSkillsDir,
    SkillsConfig config,
    IFileSystem? fileSystem = null) : IAgentTool
{
    private readonly IFileSystem _fs = fileSystem ?? new FileSystem();

    // Allowed sub-directories for write_file / remove_file supporting files
    private static readonly HashSet<string> AllowedSupportingDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "references", "templates", "scripts", "assets"
    };

    private const long MaxSkillFileBytes = 524_288; // 512 KB
    private const int MaxSupportingFileBytes = 1_048_576; // 1 MB

    public string Name => "skill_manage";
    public string Label => "Skill Manager (Write)";

    public Tool Definition => new(
        Name,
        "Create, edit, patch, and delete skills, and manage supporting files within a skill directory. Only available when skill management is enabled for this agent.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["create", "edit", "patch", "delete", "write_file", "remove_file"],
                  "description": "Action to perform: 'create' — create a new skill; 'edit' — full rewrite of SKILL.md; 'patch' — targeted find-replace within a skill file; 'delete' — remove a skill; 'write_file' — write a supporting file (references/, templates/, scripts/, assets/); 'remove_file' — delete a supporting file."
                },
                "name": {
                  "type": "string",
                  "description": "Skill name (required for all actions). Must be lowercase alphanumeric + hyphens, max 64 chars."
                },
                "content": {
                  "type": "string",
                  "description": "Full SKILL.md content including YAML frontmatter (required for create and edit)."
                },
                "oldText": {
                  "type": "string",
                  "description": "Exact text to find (required for patch)."
                },
                "newText": {
                  "type": "string",
                  "description": "Replacement text (required for patch)."
                },
                "filePath": {
                  "type": "string",
                  "description": "Relative path within the skill directory for patch, write_file, and remove_file. Defaults to SKILL.md for patch."
                },
                "replaceAll": {
                  "type": "boolean",
                  "description": "Replace all occurrences in patch (default: false, replaces first occurrence only)."
                }
              },
              "required": ["action", "name"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(arguments);
    }

    public Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!config.AllowSkillCreation)
            return Task.FromResult(Error("Skill management is not enabled for this agent."));

        var action = ReadString(arguments, "action") ?? string.Empty;
        var name = ReadString(arguments, "name") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(Error("'name' is required."));

        if (!SkillParser.IsValidName(name))
            return Task.FromResult(Error($"Invalid skill name '{name}'. Must be lowercase alphanumeric + hyphens, no leading/trailing/consecutive hyphens, max 64 chars."));

        return action.ToLowerInvariant() switch
        {
            "create" => Task.FromResult(CreateSkill(name, arguments)),
            "edit" => Task.FromResult(EditSkill(name, arguments)),
            "patch" => Task.FromResult(PatchSkill(name, arguments)),
            "delete" => Task.FromResult(DeleteSkill(name)),
            "write_file" => Task.FromResult(WriteFile(name, arguments)),
            "remove_file" => Task.FromResult(RemoveFile(name, arguments)),
            _ => Task.FromResult(Error($"Unknown action: '{action}'. Valid: create, edit, patch, delete, write_file, remove_file."))
        };
    }

    // ──────────────────────────── create ────────────────────────────────────

    private AgentToolResult CreateSkill(string name, IReadOnlyDictionary<string, object?> arguments)
    {
        var content = ReadString(arguments, "content");
        if (string.IsNullOrWhiteSpace(content))
            return Error("'content' is required for create.");

        var targetDir = ResolveWriteRoot(name);
        if (targetDir is null)
            return Error("No writable skill directory is configured for this agent.");

        var skillDir = Path.Combine(targetDir, name);
        var skillMdPath = Path.Combine(skillDir, "SKILL.md");

        if (_fs.Directory.Exists(skillDir))
            return Error($"Skill '{name}' already exists at '{skillDir}'. Use 'edit' to update it.");

        if (content.Length > MaxSkillFileBytes)
            return Error($"SKILL.md content exceeds maximum size of {MaxSkillFileBytes / 1024} KB.");

        // Validate parseable + required fields before writing
        var parsed = SkillParser.Parse(name, content, skillDir, SkillSource.Workspace);
        if (string.IsNullOrWhiteSpace(parsed.Description))
            return Error("SKILL.md frontmatter must include a non-empty 'description' field.");

        if (!string.Equals(parsed.Name, name, StringComparison.Ordinal))
            return Error($"Frontmatter 'name' field ('{parsed.Name}') must match the skill directory name '{name}'.");

        _fs.Directory.CreateDirectory(skillDir);
        _fs.File.WriteAllText(skillMdPath, content);

        // Security scan post-write — roll back on critical finding
        var scan = SkillSecurityScanner.ScanDirectory(skillDir, fileSystem: _fs);
        if (scan.Critical > 0)
        {
            _fs.Directory.Delete(skillDir, recursive: true);
            return Error($"Skill creation blocked: security scan found {scan.Critical} critical finding(s). Rolled back.\n{FormatFindings(scan)}");
        }

        return Ok($"Skill '{name}' created at '{skillDir}'.\nSecurity scan: {scan.ScannedFiles} file(s) scanned, {scan.Warn} warning(s).");
    }

    // ──────────────────────────── edit ──────────────────────────────────────

    private AgentToolResult EditSkill(string name, IReadOnlyDictionary<string, object?> arguments)
    {
        var content = ReadString(arguments, "content");
        if (string.IsNullOrWhiteSpace(content))
            return Error("'content' is required for edit.");

        var skillDir = FindSkillDir(name, out var source);
        if (skillDir is null)
            return Error($"Skill '{name}' not found in any configured skill directory.");

        if (source == SkillSource.Global)
            return Error($"Skill '{name}' is a global skill and cannot be edited by the agent. Copy it to the agent or workspace scope first.");

        var skillMdPath = Path.Combine(skillDir, "SKILL.md");

        if (content.Length > MaxSkillFileBytes)
            return Error($"SKILL.md content exceeds maximum size of {MaxSkillFileBytes / 1024} KB.");

        var parsed = SkillParser.Parse(name, content, skillDir, source);
        if (string.IsNullOrWhiteSpace(parsed.Description))
            return Error("SKILL.md frontmatter must include a non-empty 'description' field.");

        if (!string.Equals(parsed.Name, name, StringComparison.Ordinal))
            return Error($"Frontmatter 'name' field ('{parsed.Name}') must match the skill directory name '{name}'.");

        // Read backup before overwrite
        var backup = _fs.File.Exists(skillMdPath) ? _fs.File.ReadAllText(skillMdPath) : null;

        _fs.File.WriteAllText(skillMdPath, content);

        var scan = SkillSecurityScanner.ScanDirectory(skillDir, fileSystem: _fs);
        if (scan.Critical > 0)
        {
            // Roll back
            if (backup is not null)
                _fs.File.WriteAllText(skillMdPath, backup);
            else
                _fs.File.Delete(skillMdPath);

            return Error($"Skill edit blocked: security scan found {scan.Critical} critical finding(s). Rolled back.\n{FormatFindings(scan)}");
        }

        return Ok($"Skill '{name}' updated at '{skillDir}'.\nSecurity scan: {scan.ScannedFiles} file(s) scanned, {scan.Warn} warning(s).");
    }

    // ──────────────────────────── patch ─────────────────────────────────────

    private AgentToolResult PatchSkill(string name, IReadOnlyDictionary<string, object?> arguments)
    {
        var oldText = ReadString(arguments, "oldText");
        var newText = ReadString(arguments, "newText");

        if (oldText is null)
            return Error("'oldText' is required for patch.");
        if (newText is null)
            return Error("'newText' is required for patch.");

        var skillDir = FindSkillDir(name, out var source);
        if (skillDir is null)
            return Error($"Skill '{name}' not found in any configured skill directory.");

        if (source == SkillSource.Global)
            return Error($"Skill '{name}' is a global skill and cannot be patched by the agent.");

        var relPath = ReadString(arguments, "filePath") ?? "SKILL.md";
        if (!IsAllowedFilePath(relPath, out var reason))
            return Error(reason);

        var targetPath = Path.Combine(skillDir, relPath);

        // Validate symlink resolution stays within skill boundary
        if (!SkillPathValidator.TryValidate(targetPath, skillDir, _fs, out targetPath, out var symlinkError))
            return Error(symlinkError!);

        if (!_fs.File.Exists(targetPath))
            return Error($"File '{relPath}' not found in skill '{name}'.");

        var current = _fs.File.ReadAllText(targetPath);

        if (!current.Contains(oldText, StringComparison.Ordinal))
            return Error($"'oldText' not found in '{relPath}'. No changes made.");

        var replaceAll = ReadBool(arguments, "replaceAll") ?? false;
        var updated = replaceAll
            ? current.Replace(oldText, newText, StringComparison.Ordinal)
            : ReplaceFirst(current, oldText, newText);

        // If patching SKILL.md, validate frontmatter still intact
        if (string.Equals(relPath, "SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = SkillParser.Parse(name, updated, skillDir, source);
            if (string.IsNullOrWhiteSpace(parsed.Description))
                return Error("Patch would remove the required 'description' frontmatter field. Rejected.");
        }

        var backup = current;
        _fs.File.WriteAllText(targetPath, updated);

        // Security scan
        var scan = SkillSecurityScanner.ScanDirectory(skillDir, fileSystem: _fs);
        if (scan.Critical > 0)
        {
            _fs.File.WriteAllText(targetPath, backup);
            return Error($"Patch blocked: security scan found {scan.Critical} critical finding(s). Rolled back.\n{FormatFindings(scan)}");
        }

        var occurrences = replaceAll ? CountOccurrences(current, oldText) : 1;
        return Ok($"Patched '{relPath}' in skill '{name}' ({occurrences} replacement(s)).\nSecurity scan: {scan.ScannedFiles} file(s) scanned, {scan.Warn} warning(s).");
    }

    // ──────────────────────────── delete ────────────────────────────────────

    private AgentToolResult DeleteSkill(string name)
    {
        if (!config.AllowSkillDeletion)
            return Error("Skill deletion is not enabled for this agent (AllowSkillDeletion = false).");

        var skillDir = FindSkillDir(name, out var source);
        if (skillDir is null)
            return Error($"Skill '{name}' not found.");

        if (source == SkillSource.Global)
            return Error($"Skill '{name}' is a global skill and cannot be deleted by the agent.");

        _fs.Directory.Delete(skillDir, recursive: true);
        return Ok($"Skill '{name}' deleted from '{skillDir}'.");
    }

    // ──────────────────────────── write_file ────────────────────────────────

    private AgentToolResult WriteFile(string name, IReadOnlyDictionary<string, object?> arguments)
    {
        var relPath = ReadString(arguments, "filePath");
        if (string.IsNullOrWhiteSpace(relPath))
            return Error("'filePath' is required for write_file.");

        if (string.Equals(relPath, "SKILL.md", StringComparison.OrdinalIgnoreCase))
            return Error("Use 'create' or 'edit' to modify SKILL.md, not 'write_file'.");

        if (!IsAllowedFilePath(relPath, out var reason))
            return Error(reason);

        var content = ReadString(arguments, "content") ?? string.Empty;

        if (content.Length > MaxSupportingFileBytes)
            return Error($"File content exceeds maximum size of {MaxSupportingFileBytes / 1024} KB.");

        var skillDir = FindSkillDir(name, out var source);
        if (skillDir is null)
        {
            // Skill must exist before writing supporting files
            return Error($"Skill '{name}' not found. Create the skill first with 'create'.");
        }

        if (source == SkillSource.Global)
            return Error($"Skill '{name}' is a global skill. Supporting files cannot be written to it by the agent.");

        var targetPath = Path.Combine(skillDir, relPath);

        // Validate symlink resolution stays within skill boundary
        if (!SkillPathValidator.TryValidate(targetPath, skillDir, _fs, out targetPath, out var writeSymlinkError))
            return Error(writeSymlinkError!);

        // Ensure the supporting sub-directory exists
        var targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir))
            _fs.Directory.CreateDirectory(targetDir);

        _fs.File.WriteAllText(targetPath, content);

        // Security scan
        var scan = SkillSecurityScanner.ScanDirectory(skillDir, fileSystem: _fs);
        if (scan.Critical > 0)
        {
            _fs.File.Delete(targetPath);
            return Error($"File write blocked: security scan found {scan.Critical} critical finding(s). Rolled back.\n{FormatFindings(scan)}");
        }

        return Ok($"Wrote '{relPath}' in skill '{name}'.\nSecurity scan: {scan.ScannedFiles} file(s) scanned, {scan.Warn} warning(s).");
    }

    // ──────────────────────────── remove_file ───────────────────────────────

    private AgentToolResult RemoveFile(string name, IReadOnlyDictionary<string, object?> arguments)
    {
        if (!config.AllowSkillDeletion)
            return Error("Skill deletion is not enabled for this agent (AllowSkillDeletion = false).");

        var relPath = ReadString(arguments, "filePath");
        if (string.IsNullOrWhiteSpace(relPath))
            return Error("'filePath' is required for remove_file.");

        if (string.Equals(relPath, "SKILL.md", StringComparison.OrdinalIgnoreCase))
            return Error("SKILL.md cannot be removed with 'remove_file'. Use 'delete' to remove the entire skill.");

        if (!IsAllowedFilePath(relPath, out var reason))
            return Error(reason);

        var skillDir = FindSkillDir(name, out var source);
        if (skillDir is null)
            return Error($"Skill '{name}' not found.");

        if (source == SkillSource.Global)
            return Error($"Skill '{name}' is a global skill. Files cannot be removed from it by the agent.");

        var targetPath = Path.Combine(skillDir, relPath);

        // Validate symlink resolution stays within skill boundary
        if (!SkillPathValidator.TryValidate(targetPath, skillDir, _fs, out targetPath, out var removeSymlinkError))
            return Error(removeSymlinkError!);

        if (!_fs.File.Exists(targetPath))
            return Error($"File '{relPath}' not found in skill '{name}'.");

        _fs.File.Delete(targetPath);
        return Ok($"Removed '{relPath}' from skill '{name}'.");
    }

    // ──────────────────────────── helpers ───────────────────────────────────

    /// <summary>
    /// Resolves the write-target root for a new skill.
    /// Prefers workspace, falls back to agent. Never writes to global.
    /// </summary>
    private string? ResolveWriteRoot(string name)
    {
        if (!string.IsNullOrWhiteSpace(workspaceSkillsDir))
            return workspaceSkillsDir;
        if (!string.IsNullOrWhiteSpace(agentSkillsDir))
            return agentSkillsDir;
        return null;
    }

    /// <summary>
    /// Finds the skill directory for the given skill name across agent and workspace sources.
    /// Global source is included for read (find) but write operations must reject it.
    /// </summary>
    private string? FindSkillDir(string name, out SkillSource source)
    {
        // Workspace overrides agent which overrides global — match SkillDiscovery priority
        foreach (var (dir, src) in new[] {
            (workspaceSkillsDir, SkillSource.Workspace),
            (agentSkillsDir, SkillSource.Agent) })
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            var candidate = Path.Combine(dir, name);
            if (_fs.Directory.Exists(candidate) && _fs.File.Exists(Path.Combine(candidate, "SKILL.md")))
            {
                source = src;
                return candidate;
            }
        }

        source = SkillSource.Global;
        return null;
    }

    /// <summary>Validates a relative file path inside a skill directory.</summary>
    private static bool IsAllowedFilePath(string relPath, out string reason)
    {
        reason = string.Empty;

        // Normalize separators
        var normalized = relPath.Replace('\\', '/').TrimStart('/');

        // Block absolute paths and traversal
        if (Path.IsPathRooted(normalized) || normalized.Contains("../") || normalized.Contains("..\\"))
        {
            reason = "Path traversal is not permitted in file paths.";
            return false;
        }

        // SKILL.md is allowed for patch target
        if (string.Equals(normalized, "SKILL.md", StringComparison.OrdinalIgnoreCase))
            return true;

        // Supporting files must be under an allowed sub-directory
        var parts = normalized.Split('/');
        if (parts.Length < 2 || !AllowedSupportingDirs.Contains(parts[0]))
        {
            reason = $"Supporting files must be under one of: {string.Join(", ", AllowedSupportingDirs)}/. Got: '{relPath}'.";
            return false;
        }

        return true;
    }

    private static string ReplaceFirst(string source, string oldText, string newText)
    {
        var index = source.IndexOf(oldText, StringComparison.Ordinal);
        if (index < 0)
            return source;
        return string.Concat(source.AsSpan(0, index), newText, source.AsSpan(index + oldText.Length));
    }

    private static int CountOccurrences(string source, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static string FormatFindings(ScanSummary scan)
    {
        if (scan.Findings.Count == 0)
            return string.Empty;

        var lines = scan.Findings
            .Where(f => f.Severity == ScanSeverity.Critical)
            .Select(f => $"  [{f.RuleId}] {f.File}:{f.Line} — {f.Message}")
            .Take(5);

        return "Critical findings:\n" + string.Join("\n", lines);
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            JsonElement el => el.ToString(),
            _ => value.ToString()
        };
    }

    private static bool? ReadBool(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement el => bool.TryParse(el.ToString(), out var b) ? b : null,
            bool b => b,
            _ => bool.TryParse(value.ToString(), out var parsed) ? parsed : null
        };
    }

    private static AgentToolResult Ok(string text)
        => new([new AgentToolContent(AgentToolContentType.Text, text)]);

    private static AgentToolResult Error(string message)
        => new([new AgentToolContent(AgentToolContentType.Text, $"Error: {message}")]);
}
