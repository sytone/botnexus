using BotNexus.Agent.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Extensions.Mcp.Plugins;

/// <summary>Outcome of registering one plugin's declared MCP servers.</summary>
/// <param name="PluginName">The plugin the registration belongs to.</param>
/// <param name="ScopedServerNames">Scoped ids the servers were registered under, in declaration order.</param>
/// <param name="Tools">Bridged tools contributed by the servers that started.</param>
/// <param name="SkippedReason">Why nothing was registered, or <c>null</c> when registration proceeded.</param>
public sealed record PluginMcpRegistration(
    string PluginName,
    IReadOnlyList<string> ScopedServerNames,
    IReadOnlyList<IAgentTool> Tools,
    string? SkippedReason)
{
    /// <summary>Whether the plugin's servers were registered at all.</summary>
    public bool Registered => SkippedReason is null;
}

/// <summary>
/// Registers the MCP servers an installed plugin declares with the existing
/// <see cref="McpServerManager"/>, under names scoped by the plugin's identity.
/// </summary>
/// <remarks>
/// Three properties drive the shape of this class.
/// <para>
/// <b>One manager, not a parallel registry.</b> Plugin servers are ordinary MCP servers and are
/// started through the same manager as configured ones. A second registry would mean a second
/// lifecycle, a second warmup path and a second place for a leaked process to hide.
/// </para>
/// <para>
/// <b>Collision is impossible by construction.</b> Every id is passed through
/// <see cref="PluginScopedServerName.Scope"/> on the way in, so two plugins declaring the same
/// server name cannot overwrite each other and there is nothing to detect and warn about.
/// </para>
/// <para>
/// <b>Trust is decided before anything is started.</b> Under
/// <see cref="PluginTrustMode.Enforce"/> an untrusted plugin's servers are never handed to the
/// manager - not started and then stopped. An MCP server is a process spawn or an outbound
/// credentialled connection, so "start it and reconsider" would already have done the damage.
/// </para>
/// </remarks>
public sealed class PluginMcpServerRegistrar
{
    private readonly IMcpServerHost _manager;
    private readonly IPluginTrustEvaluator _trust;
    private readonly ILogger _logger;
    private readonly Dictionary<string, List<string>> _registered = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Creates a registrar over an existing server manager.</summary>
    /// <param name="manager">The existing MCP server manager plugin servers are registered with.</param>
    /// <param name="trust">Trust evaluator consulted before any server is started.</param>
    /// <param name="logger">Optional logger.</param>
    public PluginMcpServerRegistrar(
        IMcpServerHost manager,
        IPluginTrustEvaluator? trust = null,
        ILogger? logger = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _trust = trust ?? DisabledPluginTrustEvaluator.Instance;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Scoped server ids currently registered for a plugin.</summary>
    /// <param name="pluginName">Plugin identifier.</param>
    public IReadOnlyList<string> GetRegisteredServerNames(string pluginName)
    {
        lock (_gate)
        {
            return _registered.TryGetValue(pluginName, out var names) ? [.. names] : [];
        }
    }

    /// <summary>
    /// Reads a plugin's MCP declaration and registers every server it declares.
    /// </summary>
    /// <param name="pluginName">Plugin identifier, which becomes the registration scope.</param>
    /// <param name="pluginDirectory">Absolute directory holding the plugin's content.</param>
    /// <param name="declaredPath">Manifest <c>mcpServers</c> value, or <c>null</c> for convention.</param>
    /// <param name="useToolPrefix">Whether bridged tool names carry the server prefix.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<PluginMcpRegistration> RegisterAsync(
        string pluginName,
        string pluginDirectory,
        string? declaredPath = null,
        bool useToolPrefix = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        var decision = _trust.Evaluate(pluginName, pluginDirectory);
        if (!decision.Trusted)
        {
            if (_trust.Mode == PluginTrustMode.Enforce)
            {
                var reason = decision.Reason ?? "Plugin failed trust verification.";
                _logger.LogWarning(
                    "Refusing to register MCP servers for untrusted plugin '{Plugin}' under Enforce: {Reason}",
                    pluginName,
                    reason);
                return new PluginMcpRegistration(pluginName, [], [], reason);
            }

            if (_trust.Mode == PluginTrustMode.Warn)
            {
                _logger.LogWarning(
                    "Registering MCP servers for plugin '{Plugin}' despite failed trust verification: {Reason}",
                    pluginName,
                    decision.Reason ?? "unknown");
            }
        }

        var declaration = PluginMcpDeclarationReader.Read(pluginDirectory, declaredPath);
        if (!declaration.IsValid)
        {
            _logger.LogWarning(
                "Plugin '{Plugin}' declares MCP servers that could not be read: {Reason}",
                pluginName,
                declaration.Error);
            return new PluginMcpRegistration(pluginName, [], [], declaration.Error);
        }

        var scopedNames = new List<string>();
        var tools = new List<IAgentTool>();

        foreach (var (declaredName, serverConfig) in declaration.Servers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var scoped = PluginScopedServerName.Scope(pluginName, declaredName);
            scopedNames.Add(scoped);

            var serverTools = await _manager
                .StartServerAsync(scoped, serverConfig, useToolPrefix, cancellationToken)
                .ConfigureAwait(false);

            tools.AddRange(serverTools);
        }

        if (scopedNames.Count > 0)
        {
            lock (_gate)
            {
                if (!_registered.TryGetValue(pluginName, out var existing))
                {
                    existing = [];
                    _registered[pluginName] = existing;
                }

                foreach (var name in scopedNames)
                {
                    if (!existing.Contains(name, StringComparer.Ordinal))
                        existing.Add(name);
                }
            }

            _logger.LogInformation(
                "Registered {Count} MCP server(s) for plugin '{Plugin}'.",
                scopedNames.Count,
                pluginName);
        }

        return new PluginMcpRegistration(pluginName, scopedNames, tools, null);
    }

    /// <summary>
    /// Unregisters every server a plugin registered, stopping the underlying connections.
    /// </summary>
    /// <remarks>
    /// Selection is by the plugin scope encoded in the server id, so removal can never take down a
    /// server another plugin - or the user's own configuration - registered.
    /// </remarks>
    /// <param name="pluginName">Plugin identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scoped server ids that were unregistered.</returns>
    public async Task<IReadOnlyList<string>> UnregisterAsync(
        string pluginName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);

        List<string> owned;
        lock (_gate)
        {
            owned = _registered.TryGetValue(pluginName, out var names) ? [.. names] : [];
            _registered.Remove(pluginName);
        }

        await _manager
            .StopServersAsync(id => PluginScopedServerName.BelongsTo(id, pluginName), cancellationToken)
            .ConfigureAwait(false);

        if (owned.Count > 0)
        {
            _logger.LogInformation(
                "Unregistered {Count} MCP server(s) for plugin '{Plugin}'.",
                owned.Count,
                pluginName);
        }

        return owned;
    }
}
