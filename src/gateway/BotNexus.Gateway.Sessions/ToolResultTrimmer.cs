// ToolResultTrimmer.cs
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Replaces large, stale tool results with compact tombstones to reclaim token budget.
/// Uses an age+size hybrid strategy: tool results exceeding
/// <see cref="ToolResultTrimmingOptions.MinContentLengthChars"/> that are older than the
/// configured turn threshold are replaced with a tombstone containing metadata and a short preview.
/// </summary>
/// <remarks>
/// Tombstone format:
/// <code>
/// [tool result trimmed — {toolName}, {originalLength} chars, produced {age} turns ago]
/// {preview}…
/// </code>
/// This class is stateless and produces a new list; it never mutates the input entries.
/// </remarks>
public sealed class ToolResultTrimmer
{
    /// <summary>Marker prefix for tombstone detection.</summary>
    public const string TombstoneMarker = "[tool result trimmed";

    private readonly ToolResultTrimmingOptions _options;

    /// <summary>Creates a new trimmer with the given options.</summary>
    public ToolResultTrimmer(ToolResultTrimmingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Applies trimming to the live context entries. Tool results that exceed the size
    /// threshold and are older than the configured turn threshold are replaced with tombstones.
    /// </summary>
    /// <param name="entries">The live context entries (output of <see cref="SessionContextProjector.ProjectForLiveContext"/>).</param>
    /// <returns>A new list with eligible tool results replaced by tombstones. Returns the same list reference if no trimming occurred.</returns>
    public IReadOnlyList<SessionEntry> Trim(IReadOnlyList<SessionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (!_options.Enabled || entries.Count == 0)
            return entries;

        // Count user turns from the end to determine age of each entry.
        int currentTurnAge = 0;
        var turnAges = new int[entries.Count];

        // Walk backward — every user message increments the turn counter.
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Role.Equals(MessageRole.User) && !entries[i].IsHistory)
                currentTurnAge++;
            turnAges[i] = currentTurnAge;
        }

        List<SessionEntry>? result = null;

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

            if (ShouldTrim(entry, turnAges[i]))
            {
                result ??= new List<SessionEntry>(entries.Count);
                // Copy everything before this that we haven't added yet
                if (result.Count == 0)
                {
                    for (int j = 0; j < i; j++)
                        result.Add(entries[j]);
                }

                result.Add(CreateTombstone(entry, turnAges[i]));
            }
            else if (result is not null)
            {
                result.Add(entry);
            }
        }

        return result ?? entries;
    }

    /// <summary>
    /// Determines whether a given entry is already a tombstone (previously trimmed).
    /// </summary>
    public static bool IsTombstone(SessionEntry entry) =>
        entry.Role.Equals(MessageRole.Tool)
        && entry.Content.StartsWith(TombstoneMarker, StringComparison.Ordinal);

    private bool ShouldTrim(SessionEntry entry, int turnAge)
    {
        // Only trim tool result entries (not tool-start entries which have ToolArgs).
        if (!entry.Role.Equals(MessageRole.Tool))
            return false;

        // Don't trim tool-start entries (they have ToolArgs set).
        if (entry.ToolArgs is not null)
            return false;

        // Already a tombstone — skip.
        if (IsTombstone(entry))
            return false;

        // Too short to bother trimming.
        if (entry.Content.Length < _options.MinContentLengthChars)
            return false;

        // Determine the age threshold for this tool.
        int threshold = _options.AgeTurnsThreshold;
        if (entry.ToolName is not null && _options.ToolThresholds.TryGetValue(entry.ToolName, out var custom))
            threshold = custom;

        return turnAge >= threshold;
    }

    private SessionEntry CreateTombstone(SessionEntry original, int turnAge)
    {
        var preview = original.Content.Length > _options.TombstonePreviewChars
            ? original.Content[.._options.TombstonePreviewChars]
            : original.Content;

        var toolName = original.ToolName ?? "unknown";
        var tombstoneContent = $"{TombstoneMarker} — {toolName}, {original.Content.Length} chars, produced {turnAge} turns ago]\n{preview}…";

        return original with { Content = tombstoneContent };
    }
}
