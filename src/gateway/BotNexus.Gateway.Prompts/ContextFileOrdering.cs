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
    /// Executes normalize path.
    /// </summary>
    /// <param name="pathValue">The path value.</param>
    /// <returns>The normalize path result.</returns>
    public static string NormalizePath(string pathValue) =>
        pathValue.Trim().Replace('\\', '/');

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
        if (DefaultOrder.TryGetValue(GetBasename(path), out var order))
            return order;

        return IsDailyMemoryNote(path) ? 75 : int.MaxValue;
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
