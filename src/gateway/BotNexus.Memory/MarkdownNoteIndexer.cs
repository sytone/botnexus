using System.Globalization;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotNexus.Memory.Models;
using BotNexus.Domain.Text;

using BotNexus.Memory.Tools;

namespace BotNexus.Memory;

/// <summary>
/// Indexes markdown notes written through the workspace file path into the searchable
/// memory store, so <c>memory_search</c> can retrieve deliberately-curated daily notes and
/// not only conversation turns (issue #2780).
/// </summary>
/// <remarks>
/// <para>
/// The markdown file remains the source of truth. This type runs <i>after</i> a successful
/// workspace append and mirrors the resulting file content into the store; every failure is
/// swallowed by the caller so an indexing problem can never lose a note.
/// </para>
/// <para>
/// Notes are chunked by markdown heading and each chunk is keyed by a deterministic identity
/// derived from agent + workspace-relative file path + heading. Re-indexing after an append
/// therefore <b>replaces</b> the row for a section instead of accumulating one near-duplicate
/// row per append.
/// </para>
/// </remarks>
internal static class MarkdownNoteIndexer
{
    /// <summary>Source type stamped on every markdown-note row; the selector for notes vs conversation turns.</summary>
    internal const string NoteSourceType = "note";

    /// <summary>Section name used for content that appears before the first markdown heading.</summary>
    internal const string PreambleHeading = "(preamble)";

    /// <summary>
    /// Reads the note file that was just appended to and upserts one store row per markdown
    /// section. Returns the number of sections indexed.
    /// </summary>
    internal static async Task<int> IndexNoteFileAsync(
        IMemoryStore memoryStore,
        IFileSystem fileSystem,
        string agentId,
        string workspacePath,
        string notePath,
        CancellationToken ct)
    {
        if (!fileSystem.File.Exists(notePath))
            return 0;

        var content = await fileSystem.File.ReadAllTextAsync(notePath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
            return 0;

        var relativePath = ToRelativeNotePath(fileSystem, workspacePath, notePath);
        var now = DateTimeOffset.UtcNow;
        var indexed = 0;

        await memoryStore.InitializeAsync(ct).ConfigureAwait(false);

        foreach (var section in SplitSections(content))
        {
            ct.ThrowIfCancellationRequested();

            var id = BuildSectionId(agentId, relativePath, section.Heading);
            var entry = new MemoryEntry
            {
                Id = id,
                AgentId = agentId,
                SessionId = null,
                TurnIndex = null,
                SourceType = NoteSourceType,
                // A note under the agent's memory root was written by the agent itself, so it is
                // first-party `agent` content (#2480). Note bodies may quote untrusted text, which
                // is why the sanitiser below still runs - provenance records origin, it does not
                // replace sanitisation.
                //
                // #2519: a section carrying the quarantine marker was written on a run that
                // consumed foreign content, so it is downgraded to external-untrusted here. The
                // marker is checked per SECTION rather than per file because a single daily note
                // accumulates writes from many runs, only some of which were tainted; stamping the
                // whole file from one section's marker would either over- or under-report.
                Provenance = MemoryQuarantine.IsQuarantined(section.Content)
                    ? MemoryProvenance.ExternalUntrusted
                    : MemoryProvenance.Agent,
                // Same sanitisation contract as the conversation writer (#1560): note bodies can
                // contain text an agent copied verbatim from an untrusted inbound message.
                Content = UntrustedContentSanitizer.Sanitize(section.Content),
                MetadataJson = JsonSerializer.Serialize(new NoteMetadata(relativePath, section.Heading)),
                Embedding = null,
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = null,
                IsArchived = false
            };

            // Delete-then-insert is the upsert: the identity is stable per file+section, so the
            // twentieth append to memory/YYYY-MM-DD.md replaces its section rows rather than
            // adding twenty near-identical ones.
            await memoryStore.DeleteAsync(id, ct).ConfigureAwait(false);
            await memoryStore.InsertAsync(entry, ct).ConfigureAwait(false);
            indexed++;
        }

        return indexed;
    }

    /// <summary>
    /// Resolves the markdown file the workspace manager appended to, mirroring
    /// <c>FileAgentWorkspaceManager</c> path resolution. Returns null when the request escapes
    /// the memory root, in which case nothing is indexed.
    /// </summary>
    internal static string? ResolveNotePath(
        IFileSystem fileSystem,
        string workspacePath,
        string? memoryPathOverride,
        string? filePath)
    {
        var workspaceFullPath = fileSystem.Path.GetFullPath(workspacePath);
        var relative = string.IsNullOrWhiteSpace(memoryPathOverride)
            ? "memory"
            : memoryPathOverride.Trim().Replace('\\', '/');

        if (fileSystem.Path.IsPathRooted(relative))
            return null;

        var overrideFullPath = fileSystem.Path.GetFullPath(fileSystem.Path.Combine(workspaceFullPath, relative));
        if (!IsWithinRoot(fileSystem, workspaceFullPath, overrideFullPath))
            overrideFullPath = fileSystem.Path.Combine(workspaceFullPath, "memory");

        string memoryRoot;
        string? defaultTargetPath = null;
        if (overrideFullPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            memoryRoot = fileSystem.Path.GetDirectoryName(overrideFullPath) is { Length: > 0 } dir
                ? dir
                : fileSystem.Path.Combine(workspaceFullPath, "memory");
            defaultTargetPath = overrideFullPath;
        }
        else
        {
            memoryRoot = overrideFullPath;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return defaultTargetPath ?? fileSystem.Path.Combine(
                memoryRoot,
                $"{DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.md");
        }

        if (fileSystem.Path.IsPathRooted(filePath))
            return null;

        var normalized = filePath.Trim();
        if (normalized.StartsWith("memory/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("memory\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[("memory".Length + 1)..];
        }

        var resolved = fileSystem.Path.GetFullPath(fileSystem.Path.Combine(memoryRoot, normalized));
        return IsWithinRoot(fileSystem, memoryRoot, resolved) ? resolved : null;
    }

    /// <summary>Splits markdown into one chunk per heading, with any leading text as a preamble chunk.</summary>
    internal static IReadOnlyList<NoteSection> SplitSections(string content)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<NoteSection> sections = [];
        var heading = PreambleHeading;
        var buffer = new StringBuilder();

        void Flush()
        {
            var text = buffer.ToString().Trim();
            if (text.Length > 0)
                sections.Add(new NoteSection(heading, text));
            buffer.Clear();
        }

        foreach (var line in lines)
        {
            if (IsHeading(line))
            {
                Flush();
                heading = line.TrimStart('#', ' ', '\t').Trim();
                if (heading.Length == 0)
                    heading = PreambleHeading;
            }

            buffer.AppendLine(line);
        }

        Flush();
        return sections;
    }

    private static bool IsHeading(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('#'))
            return false;

        var hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#')
            hashes++;

        // ATX headings require whitespace after the run of hashes; this also rejects '#tag'.
        return hashes <= 6 && hashes < trimmed.Length && char.IsWhiteSpace(trimmed[hashes]);
    }

    /// <summary>Deterministic 32-char identity for a note section so appends update in place.</summary>
    internal static string BuildSectionId(string agentId, string relativePath, string heading)
    {
        var key = $"note|{agentId}|{relativePath}|{heading}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(hash)[..32];
    }

    private static string ToRelativeNotePath(IFileSystem fileSystem, string workspacePath, string notePath)
    {
        var workspaceFullPath = fileSystem.Path.GetFullPath(workspacePath);
        var noteFullPath = fileSystem.Path.GetFullPath(notePath);
        if (!IsWithinRoot(fileSystem, workspaceFullPath, noteFullPath))
            return noteFullPath.Replace('\\', '/');

        return noteFullPath[(workspaceFullPath.TrimEnd(
            fileSystem.Path.DirectorySeparatorChar,
            fileSystem.Path.AltDirectorySeparatorChar).Length + 1)..].Replace('\\', '/');
    }

    private static bool IsWithinRoot(IFileSystem fileSystem, string root, string path)
    {
        var rootFullPath = fileSystem.Path.GetFullPath(root).TrimEnd(
            fileSystem.Path.DirectorySeparatorChar,
            fileSystem.Path.AltDirectorySeparatorChar);
        var prefix = rootFullPath + fileSystem.Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
               path.Equals(rootFullPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A single heading-delimited chunk of a markdown note.</summary>
    internal readonly record struct NoteSection(string Heading, string Content);

    private sealed record NoteMetadata(string filePath, string heading);
}
