namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Represents context file ordering.
/// </summary>
public static class ContextFileOrdering
{
    private static readonly Dictionary<string, int> DefaultOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["world.md"] = 5,
        ["agents.md"] = 10,
        ["soul.md"] = 20,
        ["identity.md"] = 30,
        ["user.md"] = 40,
        ["tools.md"] = 50,
        ["bootstrap.md"] = 60,
        ["memory.md"] = 70
    };

    /// <summary>
    /// Executes sort for prompt.
    /// </summary>
    /// <param name="contextFiles">The context files.</param>
    /// <returns>The sort for prompt result.</returns>
    public static IReadOnlyList<ContextFile> SortForPrompt(IReadOnlyList<ContextFile> contextFiles)
    {
        ArgumentNullException.ThrowIfNull(contextFiles);

        return contextFiles
            .OrderBy(file => GetOrder(file.Path))
            .ThenBy(file => GetBasename(file.Path), StringComparer.Ordinal)
            .ThenBy(file => NormalizePath(file.Path), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Executes is dynamic.
    /// </summary>
    /// <param name="pathValue">The path value.</param>
    /// <returns>The is dynamic result.</returns>
    public static bool IsDynamic(string pathValue) =>
        string.Equals(GetBasename(pathValue), "heartbeat.md", StringComparison.Ordinal) ||
        IsDailyMemoryNote(pathValue);

    /// <summary>
    /// Produces the canonical comparison key for a context-file path: trimmed, forward-slashed,
    /// with any leading <c>./</c> segments and repeated separators collapsed.
    /// </summary>
    /// <remarks>
    /// The seam serves two needs and one normalizer is safe for both (#2940). As an ORDERING key
    /// (<see cref="SortForPrompt"/>, <see cref="GetBasename"/>) the leading <c>./</c> is pure noise
    /// — collapsing it changes no relative order, and it makes <c>./memory/2026-08-11.md</c> match
    /// the <c>memory/</c> daily-note prefix test as it always should have. As an IDENTITY key
    /// (<c>AddContextFilesWithoutDuplicates</c>) the collapse is required, or an operator writing
    /// <c>./memory/{date}.md</c> in <c>systemPromptFiles</c> defeats the de-duplication and the note
    /// is emitted twice.
    /// <para>
    /// <c>..</c> segments are deliberately NOT resolved: workspace containment is the sole
    /// responsibility of <c>IsPathUnderWorkspace</c>, and duplicating it here would split a security
    /// check across two files. This returns a comparison key only — callers keep the original path.
    /// </para>
    /// </remarks>
    /// <param name="pathValue">The path value.</param>
    /// <returns>The normalized comparison key.</returns>
    public static string NormalizePath(string pathValue)
    {
        var normalized = pathValue.Trim().Replace('\\', '/');

        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);

        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];

        return normalized == "." ? string.Empty : normalized;
    }

    /// <summary>
    /// Executes get basename.
    /// </summary>
    /// <param name="pathValue">The path value.</param>
    /// <returns>The get basename result.</returns>
    public static string GetBasename(string pathValue)
    {
        var normalizedPath = NormalizePath(pathValue);
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return (segments.LastOrDefault() ?? normalizedPath).ToLowerInvariant();
    }

    private static int GetOrder(string path)
    {
        var basename = GetBasename(path);
        if (DefaultOrder.TryGetValue(basename, out var order))
            return order;

        // A model-specific variant sorts at its BASE file's position (#2435). Without this,
        // agents.gpt.md misses the basename lookup, falls through to int.MaxValue and sorts after
        // MEMORY.md -- so choosing a variant would silently REORDER the instruction stack, which
        // is a behaviour change the author never asked for.
        //
        // The grammar is tested against the CASE-PRESERVED name, not the lowercased ordering key.
        // GetBasename lowercases, which would make AGENTS.GPT.md look grammatical to the sorter
        // while ContextFileVariants -- correctly -- refuses to resolve it. One seam calling a name
        // a variant and the other refusing is exactly the divergence the shared grammar exists to
        // prevent, so an uppercase name is an ordinary unrecognised file to BOTH.
        var rawBasename = GetRawBasename(path);
        var baseName = ContextFileVariants.GetBaseFileName(rawBasename);
        if (!string.Equals(baseName, rawBasename, StringComparison.Ordinal)
            && DefaultOrder.TryGetValue(baseName, out var variantOrder))
            return variantOrder;

        return IsDailyMemoryNote(path) ? 75 : int.MaxValue;
    }

    /// <summary>
    /// The final path segment with its original casing intact. <see cref="GetBasename"/> lowercases
    /// because it is an ordering/identity key; the variant grammar is case-SENSITIVE and needs the
    /// name as the author actually spelled it.
    /// </summary>
    private static string GetRawBasename(string pathValue)
    {
        var normalizedPath = NormalizePath(pathValue);
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.LastOrDefault() ?? normalizedPath;
    }

    private static bool IsDailyMemoryNote(string path)
    {
        var normalized = NormalizePath(path);
        if (!normalized.StartsWith("memory/", StringComparison.OrdinalIgnoreCase))
            return false;

        var basename = GetBasename(path);
        if (!basename.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return false;

        var datePart = basename[..^3];
        return DateOnly.TryParseExact(datePart, "yyyy-MM-dd", out _);
    }
}
