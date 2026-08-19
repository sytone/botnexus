using BotNexus.Agent.Core.Tools;

namespace BotNexus.Extensions.Mcp.Plugins;

/// <summary>
/// The narrow slice of <see cref="McpServerManager"/> that plugin registration needs.
/// </summary>
/// <remarks>
/// This exists to make the registration policy - scoping, trust, ownership - testable without
/// spawning real MCP server processes, NOT to permit a second server implementation. The only
/// production implementation is <see cref="McpServerManagerHost"/>, which delegates straight to the
/// existing manager, and an architecture fence pins that so a parallel registry cannot appear
/// behind this interface.
/// </remarks>
public interface IMcpServerHost
{
    /// <summary>Starts one server under the supplied id and returns its bridged tools.</summary>
    /// <param name="serverId">Registration id - already plugin-scoped by the caller.</param>
    /// <param name="serverConfig">Server configuration.</param>
    /// <param name="useToolPrefix">Whether bridged tool names carry the server prefix.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<IAgentTool>> StartServerAsync(
        string serverId,
        McpServerConfig serverConfig,
        bool useToolPrefix,
        CancellationToken cancellationToken = default);

    /// <summary>Stops every running server whose id satisfies <paramref name="predicate"/>.</summary>
    /// <param name="predicate">Selects server ids to stop.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ids actually stopped.</returns>
    Task<IReadOnlyList<string>> StopServersAsync(
        Func<string, bool> predicate,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapts the existing <see cref="McpServerManager"/> to <see cref="IMcpServerHost"/>.
/// </summary>
/// <remarks>
/// Deliberately contains no logic beyond delegation. Plugin-declared servers must live in the same
/// manager, with the same lifecycle and the same teardown, as every other MCP server; anything this
/// adapter did on its own would be exactly the divergence the issue set out to avoid.
/// </remarks>
public sealed class McpServerManagerHost(McpServerManager manager) : IMcpServerHost
{
    private readonly McpServerManager _manager = manager ?? throw new ArgumentNullException(nameof(manager));

    /// <inheritdoc />
    public Task<IReadOnlyList<IAgentTool>> StartServerAsync(
        string serverId,
        McpServerConfig serverConfig,
        bool useToolPrefix,
        CancellationToken cancellationToken = default)
        => _manager.StartSingleServerAsync(serverId, serverConfig, useToolPrefix, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> StopServersAsync(
        Func<string, bool> predicate,
        CancellationToken cancellationToken = default)
        => _manager.StopServersAsync(predicate, cancellationToken);
}
