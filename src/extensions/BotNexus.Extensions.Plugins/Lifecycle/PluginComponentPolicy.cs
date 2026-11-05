namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>One bounded vocabulary for inactive foreign components and public diagnostics.</summary>
public static class PluginComponentPolicy
{
    private static readonly string[] Categories = ["agents", "commands", "hooks", "mcp", "lsp"];

    /// <summary>Maps a known foreign manifest field to its inactive category; unknown fields stay errors.</summary>
    internal static string? CategoryForField(string field) => field switch
    {
        "agents" => "agents",
        "commands" => "commands",
        "hooks" => "hooks",
        "mcpServers" => "mcp",
        "lspServers" => "lsp",
        _ => null,
    };

    /// <summary>Collects conventional component presence without opening or interpreting payloads.</summary>
    internal static IReadOnlyList<string> Discover(string directory)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, category) in new (string, string)[]
        {
            ("agents", "agents"), ("commands", "commands"), ("hooks", "hooks"),
            ("lsp", "lsp"), (".lsp.json", "lsp"), (".mcp.json", "mcp"),
            ("mcp.json", "mcp"), (".botnexus-plugin/mcp.json", "mcp"),
            ("com.github.copilot", "mcp"),
        })
        {
            var fullPath = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath) || Directory.Exists(fullPath)) found.Add(category);
        }
        return Normalize(found);
    }

    /// <summary>Filters persisted diagnostics to the fixed, bounded vocabulary in stable order.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> categories) =>
        Categories.Where(category => categories.Contains(category, StringComparer.Ordinal)).ToArray();

    /// <summary>Returns fixed messages only: no publisher text, paths, commands or credentials.</summary>
    public static IReadOnlyList<string> Warnings(bool skillsOnly, IEnumerable<string> skipped)
    {
        if (!skillsOnly) return [];
        return new[] { "Skills-only support: foreign components remain inactive." }
            .Concat(Normalize(skipped).Select(category => $"Skipped {category}: preserved as inactive content (partial support)."))
            .ToArray();
    }

    internal static IReadOnlyList<string> Declared(PluginManifest manifest) => Normalize(new[]
    {
        manifest.Agents is null ? "" : "agents",
        manifest.Commands is null ? "" : "commands",
        manifest.Hooks is null ? "" : "hooks",
        manifest.McpServers is null ? "" : "mcp",
    });
}
