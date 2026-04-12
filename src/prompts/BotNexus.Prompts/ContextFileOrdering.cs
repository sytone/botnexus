namespace BotNexus.Prompts;

public static class ContextFileOrdering
{
    private static readonly Dictionary<string, int> DefaultOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["agents.md"] = 10,
        ["soul.md"] = 20,
        ["identity.md"] = 30,
        ["user.md"] = 40,
        ["tools.md"] = 50,
        ["bootstrap.md"] = 60,
        ["memory.md"] = 70
    };

    public static IReadOnlyList<ContextFile> SortForPrompt(IReadOnlyList<ContextFile> contextFiles)
    {
        ArgumentNullException.ThrowIfNull(contextFiles);

        return contextFiles
            .OrderBy(file => DefaultOrder.TryGetValue(GetBasename(file.Path), out var order) ? order : int.MaxValue)
            .ThenBy(file => GetBasename(file.Path), StringComparer.Ordinal)
            .ThenBy(file => NormalizePath(file.Path), StringComparer.Ordinal)
            .ToList();
    }

    public static bool IsDynamic(string pathValue) =>
        string.Equals(GetBasename(pathValue), "heartbeat.md", StringComparison.Ordinal);

    public static string NormalizePath(string pathValue) =>
        pathValue.Trim().Replace('\\', '/');

    public static string GetBasename(string pathValue)
    {
        var normalizedPath = NormalizePath(pathValue);
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return (segments.LastOrDefault() ?? normalizedPath).ToLowerInvariant();
    }
}