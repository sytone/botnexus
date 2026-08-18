using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Mcp.Plugins;

/// <summary>
/// On-disk shape of a plugin's MCP server declaration file.
/// </summary>
/// <remarks>
/// The wrapper form (<c>{ "mcpServers": { ... } }</c>) is the primary shape because it matches the
/// manifest key that points at this file. A bare map at the document root is also accepted, since
/// that is what the equivalent files in the wider MCP ecosystem look like and refusing it would
/// reject a correct-looking plugin for a cosmetic reason.
/// </remarks>
public sealed class PluginMcpDeclarationFile
{
    /// <summary>Declared servers keyed by the plugin's own chosen name.</summary>
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerConfig>? McpServers { get; set; }
}

/// <summary>Outcome of reading a plugin's MCP declaration file.</summary>
/// <param name="Servers">Declared servers keyed by the plugin's own unscoped name. Empty when none.</param>
/// <param name="Error">Why the declaration could not be read, or <c>null</c> on success.</param>
public sealed record PluginMcpDeclaration(
    IReadOnlyDictionary<string, McpServerConfig> Servers,
    string? Error)
{
    /// <summary>A successful read that found no declaration at all.</summary>
    public static PluginMcpDeclaration None { get; } =
        new(new Dictionary<string, McpServerConfig>(StringComparer.Ordinal), null);

    /// <summary>Whether the declaration was readable.</summary>
    public bool IsValid => Error is null;
}

/// <summary>
/// Locates and parses the MCP server declaration belonging to an installed plugin.
/// </summary>
/// <remarks>
/// A manifest that omits <c>mcpServers</c> means "discover by convention", not "has none" - the
/// plugin manifest contract makes that distinction deliberately, so this reader honours it by
/// probing the conventional locations rather than returning empty.
/// </remarks>
public static class PluginMcpDeclarationReader
{
    /// <summary>Conventional declaration paths, probed in order when the manifest names none.</summary>
    public static IReadOnlyList<string> ConventionalPaths { get; } =
    [
        ".botnexus-plugin/mcp.json",
        ".mcp.json",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Reads the servers a plugin declares.
    /// </summary>
    /// <param name="pluginDirectory">Absolute directory holding the plugin's materialised content.</param>
    /// <param name="declaredPath">
    /// The manifest's <c>mcpServers</c> value, or <c>null</c> to discover by convention.
    /// </param>
    public static PluginMcpDeclaration Read(string pluginDirectory, string? declaredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        var root = Path.GetFullPath(pluginDirectory);
        string? file = null;

        if (!string.IsNullOrWhiteSpace(declaredPath))
        {
            // An explicit path is still confined to the plugin directory. A manifest is authored by
            // whoever wrote the plugin, so treating its path as trusted would let a plugin point the
            // loader at any file on the host and have its contents parsed as server configuration.
            var candidate = Path.GetFullPath(Path.Combine(root, declaredPath));
            if (!IsInside(root, candidate))
            {
                return new PluginMcpDeclaration(
                    PluginMcpDeclaration.None.Servers,
                    $"MCP declaration path '{declaredPath}' resolves outside the plugin directory.");
            }

            if (!File.Exists(candidate))
            {
                return new PluginMcpDeclaration(
                    PluginMcpDeclaration.None.Servers,
                    $"MCP declaration file '{declaredPath}' does not exist.");
            }

            file = candidate;
        }
        else
        {
            foreach (var conventional in ConventionalPaths)
            {
                var candidate = Path.Combine(root, conventional.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                {
                    file = candidate;
                    break;
                }
            }

            // No manifest entry and no conventional file: the plugin genuinely declares no servers.
            if (file is null)
                return PluginMcpDeclaration.None;
        }

        string json;
        try
        {
            json = File.ReadAllText(file);
        }
        catch (IOException ex)
        {
            return new PluginMcpDeclaration(PluginMcpDeclaration.None.Servers, $"Could not read '{file}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new PluginMcpDeclaration(PluginMcpDeclaration.None.Servers, $"Could not read '{file}': {ex.Message}");
        }

        try
        {
            var wrapper = JsonSerializer.Deserialize<PluginMcpDeclarationFile>(json, JsonOptions);
            if (wrapper?.McpServers is { Count: > 0 } wrapped)
            {
                return new PluginMcpDeclaration(Normalise(wrapped), null);
            }

            // Fall back to a bare root map, but only when the wrapper key was genuinely absent.
            if (wrapper?.McpServers is null)
            {
                var bare = JsonSerializer.Deserialize<Dictionary<string, McpServerConfig>>(json, JsonOptions);
                if (bare is { Count: > 0 })
                    return new PluginMcpDeclaration(Normalise(bare), null);
            }

            return PluginMcpDeclaration.None;
        }
        catch (JsonException ex)
        {
            return new PluginMcpDeclaration(
                PluginMcpDeclaration.None.Servers,
                $"MCP declaration '{file}' is not valid JSON: {ex.Message}");
        }
    }

    private static IReadOnlyDictionary<string, McpServerConfig> Normalise(
        Dictionary<string, McpServerConfig> servers)
    {
        var result = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        foreach (var (name, config) in servers)
        {
            if (string.IsNullOrWhiteSpace(name) || config is null)
                continue;

            result[name] = config;
        }

        return result;
    }

    private static bool IsInside(string root, string candidate)
    {
        var normalisedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return candidate.StartsWith(normalisedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
