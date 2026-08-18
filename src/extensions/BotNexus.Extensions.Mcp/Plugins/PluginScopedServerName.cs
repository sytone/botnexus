namespace BotNexus.Extensions.Mcp.Plugins;

/// <summary>
/// Builds and parses the server id under which a plugin-declared MCP server is registered with
/// <see cref="McpServerManager"/>.
/// </summary>
/// <remarks>
/// The scoping is applied at the moment of registration rather than checked afterwards. That is
/// the whole point: a collision between two plugins declaring the same server name is impossible
/// by construction, so there is no detect-and-warn path to get wrong, and no dependence on
/// discovery order for which plugin wins. Two plugins that both declare <c>github</c> register as
/// <c>plugin:alpha:github</c> and <c>plugin:beta:github</c> and both resolve.
/// <para>
/// The separator is deliberately a character that a plugin identifier cannot contain - plugin
/// names are lowercase kebab-case - so the scoped form stays unambiguously parseable back into
/// its two parts.
/// </para>
/// </remarks>
public static class PluginScopedServerName
{
    /// <summary>Marker identifying a server id as plugin-owned rather than user-configured.</summary>
    public const string Prefix = "plugin";

    /// <summary>Character separating the prefix, the plugin identity and the declared server name.</summary>
    public const char Separator = ':';

    /// <summary>
    /// Produces the scoped registration id for a server declared by a plugin.
    /// </summary>
    /// <param name="pluginName">Owning plugin identifier.</param>
    /// <param name="declaredServerName">Server name as written in the plugin's own MCP config.</param>
    /// <returns><c>plugin:&lt;pluginName&gt;:&lt;declaredServerName&gt;</c>.</returns>
    public static string Scope(string pluginName, string declaredServerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentException.ThrowIfNullOrWhiteSpace(declaredServerName);

        return string.Concat(Prefix, Separator, pluginName, Separator, declaredServerName);
    }

    /// <summary>
    /// Recovers the owning plugin and declared server name from a scoped registration id.
    /// </summary>
    /// <param name="scopedName">Candidate server id.</param>
    /// <param name="pluginName">Owning plugin, when the id is plugin-scoped.</param>
    /// <param name="declaredServerName">Declared server name, when the id is plugin-scoped.</param>
    /// <returns><c>true</c> only for an id produced by <see cref="Scope"/>.</returns>
    public static bool TryParse(
        string? scopedName,
        out string pluginName,
        out string declaredServerName)
    {
        pluginName = string.Empty;
        declaredServerName = string.Empty;

        if (string.IsNullOrWhiteSpace(scopedName))
            return false;

        // Split into at most three parts so a declared server name containing the separator is
        // preserved intact in the third part rather than silently truncated.
        var parts = scopedName.Split(Separator, 3);
        if (parts.Length != 3)
            return false;

        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal))
            return false;

        if (parts[1].Length == 0 || parts[2].Length == 0)
            return false;

        pluginName = parts[1];
        declaredServerName = parts[2];
        return true;
    }

    /// <summary>Whether a server id belongs to the named plugin.</summary>
    /// <param name="scopedName">Candidate server id.</param>
    /// <param name="pluginName">Plugin identifier to test ownership against.</param>
    public static bool BelongsTo(string? scopedName, string pluginName)
        => TryParse(scopedName, out var owner, out _)
           && string.Equals(owner, pluginName, StringComparison.Ordinal);
}
