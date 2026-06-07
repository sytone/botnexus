using System.IO.Abstractions;

namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Resolves prompt section overrides from markdown files on disk.
/// <para>
/// Directory layout:
/// <code>
/// {promptsDir}/
///   system/
///     {sectionId}.md          — section-level override (any model)
///     model/
///       {family}.md           — model-family-specific guidance override
/// </code>
/// </para>
/// <para>
/// Files are read fresh on every call (hot-reload, no restart required).
/// </para>
/// </summary>
public sealed class FilePromptOverrideResolver : IPromptOverrideResolver
{
    private readonly IFileSystem _fileSystem;
    private readonly string _promptsDir;

    /// <summary>
    /// Section IDs that cannot be overridden (safety-critical or runtime data).
    /// </summary>
    internal static readonly HashSet<string> NonOverridableSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "safety",
        "runtime-data",
        "runtime-info",
        "identity"
    };

    /// <summary>
    /// Creates a new <see cref="FilePromptOverrideResolver"/>.
    /// </summary>
    /// <param name="promptsDir">The root prompts directory (e.g. ~/.botnexus/prompts).</param>
    /// <param name="fileSystem">File system abstraction for testability.</param>
    public FilePromptOverrideResolver(string promptsDir, IFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptsDir);
        _promptsDir = promptsDir;
        _fileSystem = fileSystem ?? new FileSystem();
    }

    /// <inheritdoc/>
    public IReadOnlyList<string>? TryResolveOverride(string sectionId, string? modelFamily = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionId);

        if (NonOverridableSections.Contains(sectionId))
            return null;

        // For model-guidance overrides, check model/{family}.md
        if (sectionId == "model-guidance" && !string.IsNullOrWhiteSpace(modelFamily))
        {
            var modelPath = _fileSystem.Path.Combine(_promptsDir, "system", "model", $"{modelFamily}.md");
            var modelLines = TryReadFile(modelPath);
            if (modelLines is not null)
                return modelLines;
        }

        // General section override: system/{sectionId}.md
        var sectionPath = _fileSystem.Path.Combine(_promptsDir, "system", $"{sectionId}.md");
        return TryReadFile(sectionPath);
    }

    private IReadOnlyList<string>? TryReadFile(string path)
    {
        if (!_fileSystem.File.Exists(path))
            return null;

        var content = _fileSystem.File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(content))
            return null;

        return content.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .ToList();
    }
}
