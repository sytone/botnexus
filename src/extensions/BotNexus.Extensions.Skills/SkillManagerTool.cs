using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Extensions.Skills.Security;
using BotNexus.Extensions.Skills.Telemetry;
using System.IO.Abstractions;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Agent-facing tool for creating, editing, patching, deleting, and managing supporting files
/// for skills at runtime. Only contributed when <see cref="SkillsConfig.AllowSkillCreation"/>
/// is enabled. Destructive actions additionally require <see cref="SkillsConfig.AllowSkillDeletion"/>.
/// Writes to the shared (all-agent) global skills directory require
/// <see cref="SkillsConfig.AllowSharedSkillManagement"/> and are requested via the "shared" scope.
/// </summary>
public sealed class SkillManagerTool(
    string? agentSkillsDir,
    string? workspaceSkillsDir,
    string? globalSkillsDir,
    SkillsConfig config,
    IFileSystem? fileSystem = null,
    ISkillUsageTelemetry? telemetry = null,
    string? createdBy = null) : IAgentTool
{
    private readonly IFileSystem _fs = fileSystem ?? new FileSystem();

    /// <summary>
    /// Convenience overload for callers that do not manage shared (global) skills.
    /// Equivalent to passing a null global skills directory; shared scope is then unavailable.
    /// </summary>
    public SkillManagerTool(string? agentSkillsDir, string? workspaceSkillsDir, SkillsConfig config, IFileSystem? fileSystem = null)
        : this(agentSkillsDir, workspaceSkillsDir, globalSkillsDir: null, config, fileSystem)
    {
    }

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
        "Create, edit, patch, and delete skills and their supporting files. Create a skill when a complex task succeeded, errors were overcome, a user-corrected approach worked, a non-trivial workflow was discovered, or the user asks to remember a procedure. Update a skill when its instructions are stale, wrong, or missing pitfalls. Prefer patch over a full edit. A good skill has trigger conditions, numbered steps, exact commands, pitfalls, and verification steps; put reusable content in support files under references/, templates/, scripts/, or assets/. Avoid one-off PR/issue/session-artifact skills. Ask before creating or deleting skills in the foreground unless the user explicitly requested it. Only available when skill management is enabled for this agent.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["create", "edit", "patch", "delete", "write_file", "remove_file"],
                  "description": "Action to perform: 'create' - create a new skill (create when a complex task succeeded, errors were overcome, a user-corrected approach worked, a non-trivial workflow was discovered, or the user asks to remember a procedure; ask first unless explicitly requested); 'edit' - full rewrite of SKILL.md (prefer 'patch' instead); 'patch' - targeted find-replace within a skill file, preferred over 'edit' for updates when instructions are stale, wrong, or missing pitfalls; 'delete' - remove a skill (ask first unless explicitly requested); 'write_file' - write a supporting file under references/, templates/, scripts/, or assets/ for reusable content; 'remove_file' - delete a supporting file. A good skill has trigger conditions, numbered steps, exact commands, pitfalls, and verification steps; avoid one-off PR/issue/session-artifact skills."
                },
                "name": {
                  "type": "string",
                  "description": "Skill name (required for all actions). Must be lowercase alphanumeric + hyphens, max 64 chars."
                },
                "scope": {
                  "type": "string",
                  "enum": ["agent", "workspace", "shared"],
                  "description": "Where to manage the skill: 'agent' (default) - this agent's private skills; 'workspace' - the current workspace; 'shared' - the global all-agent skills directory (requires AllowSharedSkillManagement). Default is workspace. Existing skills are matched across all scopes regardless of this value."
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

        if (!TryResolveScope(arguments, out var scope, out var scopeError))
            return Task.FromResult(Error(scopeError));

        return action.ToLowerInvariant() switch
        {
            "create" => Task.FromResult(CreateSkill(name, scope, arguments)),
            "edit" => Task.FromResult(EditSkill(name, arguments)),
            "patch" => Task.FromResult(PatchSkill(name, arguments)),
            "delete" => Task.FromResult(DeleteSkill(name)),
            "write_file" => Task.FromResult(WriteFile(name, arguments)),
            "remove_file" => Task.FromResult(RemoveFile(name, arguments)),
            _ => Task.FromResult(Error($"Unknown action: '{action}'. Valid: create, edit, patch, delete, write_file, remove_file."))
        };
    }

    // ──────────────────────────── create ────────────────────────────────────

    private AgentToolResult CreateSkill(string name, SkillSource scope, IReadOnlyDictionary<string, object?> arguments)
    {
        var content = ReadString(arguments, "content");
        if (string.IsNullOrWhiteSpace(content))
            return Error("'content' is required for create.");

        var targetDir = ResolveWriteRoot(scope);
        if (targetDir is null)
            return Error("No writable skill directory is configured for this agent.");

        // Shared scope is gated; surface a clear error before touching the filesystem.
        if (scope == SkillSource.Global && !config.AllowSharedSkillManagement)
            return Error(SharedGateMessage());

        var skillDir = Path.Combine(targetDir, name);
        var skillMdPath = Path.Combine(skillDir, "SKILL.md");

        if (_fs.Directory.Exists(skillDir))
            return Error($"Skill '{name}' already exists at '{skillDir}'. Use 'edit' to update it.");

        if (content.Length > MaxSkillFileBytes)
            return Error($"SKILL.md content exceeds maximum size of {MaxSkillFileBytes / 1024} KB.");

        // Validate parseable + required fields before writing
        var parsed = SkillParser.Parse(name, content, skillDir, scope);
        if (string.IsNullOrWhiteSpace(parsed.Description))
            return Error("SKILL.md frontmatter must include a non-empty 'description' field.");

        if (!string.Equals(parsed.Name, name, StringComparison.Ordinal))
            return Error($"Frontmatter 'name' field ('{parsed.Name}') must match the skill directory name '{name}'.");

        _fs.Directory.CreateDirectory(skillDir);
        _fs.File.WriteAllText(skillMdPath, content);

        // Security scan post-write - roll back on critical finding
        var scan = SkillSecurityScanner.ScanDirectory(skillDir, fileSystem: _fs);
        if (scan.Critical > 0)
        {
            _fs.Directory.Delete(skillDir, recursive: true);
            return Error($"Skill creation blocked: security scan found {scan.Critical} critical finding(s). Rolled back.\n{FormatFindings(scan)}");
        }

        // Stamp provenance so a later curator can identify agent-created skills (#1833).
        RecordTelemetry(t => t.RecordCreatedAsync(name, createdBy ?? "unknown"));

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

        if (CheckSourceWritable(source) is { } gate)
            return gate;

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

        // A full-body edit is a mutation; count it toward the skill's patch telemetry (#1833).
        RecordTelemetry(t => t.RecordPatchAsync(name));

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

        if (CheckSourceWritable(source) is { } gate)
            return gate;

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

        // Count the patch toward the skill's telemetry (#1833).
        RecordTelemetry(t => t.RecordPatchAsync(name));

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

        if (CheckSourceWritable(source) is { } gate)
            return gate;

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

        if (CheckSourceWritable(source) is { } gate)
            return gate;

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

        // Writing a support file is a mutation of the skill; count it as a patch (#1833).
        RecordTelemetry(t => t.RecordPatchAsync(name));

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

        if (CheckSourceWritable(source) is { } gate)
            return gate;

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
    /// Resolves the write-target root for a new skill based on the requested scope.
    /// Workspace and agent scopes are unchanged; shared resolves to the global directory.
    /// Returns null when the requested scope has no configured directory.
    /// </summary>
    private string? ResolveWriteRoot(SkillSource scope) => scope switch
    {
        SkillSource.Global => string.IsNullOrWhiteSpace(globalSkillsDir) ? null : globalSkillsDir,
        SkillSource.Agent => string.IsNullOrWhiteSpace(agentSkillsDir) ? null : agentSkillsDir,
        // Workspace (default): preserve historical behaviour - prefer workspace, fall back to agent.
        _ => !string.IsNullOrWhiteSpace(workspaceSkillsDir) ? workspaceSkillsDir
            : !string.IsNullOrWhiteSpace(agentSkillsDir) ? agentSkillsDir
            : null
    };

    /// <summary>
    /// Maps the optional scope argument to a <see cref="SkillSource"/>. Defaults to agent.
    /// Returns false with an error message for an unrecognised scope.
    /// </summary>
    private static bool TryResolveScope(IReadOnlyDictionary<string, object?> arguments, out SkillSource scope, out string error)
    {
        error = string.Empty;
        var raw = ReadString(arguments, "scope");
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Default preserves historical behaviour: new skills go to workspace (falling back to agent).
            scope = SkillSource.Workspace;
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "agent":
                scope = SkillSource.Agent;
                return true;
            case "workspace":
                scope = SkillSource.Workspace;
                return true;
            case "shared":
                scope = SkillSource.Global;
                return true;
            default:
                scope = SkillSource.Agent;
                error = $"Invalid scope '{raw}'. Valid scopes: agent, workspace, shared.";
                return false;
        }
    }

    /// <summary>
    /// Gate-check for mutating an existing skill found at <paramref name="source"/>.
    /// Global (shared) skills require <see cref="SkillsConfig.AllowSharedSkillManagement"/>.
    /// Returns null when the source is writable, or an error result otherwise.
    /// </summary>
    private AgentToolResult? CheckSourceWritable(SkillSource source)
    {
        if (source == SkillSource.Global && !config.AllowSharedSkillManagement)
            return Error(SharedGateMessage());
        return null;
    }

    private static string SharedGateMessage()
        => "Shared (all-agent) skill management is not enabled (AllowSharedSkillManagement = false). Copy the skill to the agent or workspace scope first.";

    /// <summary>
    /// Finds the skill directory for the given skill name across workspace, agent, and global sources.
    /// Mirrors SkillDiscovery priority (workspace overrides agent overrides global). Global is included
    /// so shared skills are discoverable; write operations gate Global via AllowSharedSkillManagement.
    /// </summary>
    private string? FindSkillDir(string name, out SkillSource source)
    {
        foreach (var (dir, src) in new[] {
            (workspaceSkillsDir, SkillSource.Workspace),
            (agentSkillsDir, SkillSource.Agent),
            (globalSkillsDir, SkillSource.Global) })
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
            .Select(f => $"  [{f.RuleId}] {f.File}:{f.Line} - {f.Message}")
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

    /// <summary>
    /// Best-effort telemetry recording (#1833). Runs the recording action synchronously and swallows
    /// any failure: skill management must never break because the telemetry sink is unavailable or a
    /// transient SQLite lock occurs. No-ops when no telemetry sink is configured.
    /// </summary>
    private void RecordTelemetry(Func<ISkillUsageTelemetry, Task> record)
    {
        if (telemetry is null)
            return;

        try
        {
            record(telemetry).GetAwaiter().GetResult();
        }
        catch
        {
            // Telemetry is an observability side-channel; never surface its failure to the caller.
        }
    }
}
