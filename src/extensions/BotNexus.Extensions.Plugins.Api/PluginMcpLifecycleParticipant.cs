using BotNexus.Extensions.Mcp.Plugins;
using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Extensions.Plugins.Security;

namespace BotNexus.Extensions.Plugins.Api;

/// <summary>
/// Composition-side adapter: core Plugins depends only on its awaited lifecycle contract,
/// while MCP ownership, convention discovery and trust remain in the existing registrar.
/// </summary>
public sealed class PluginMcpLifecycleParticipant(PluginMcpServerRegistrar registrar) : IPluginLifecycleParticipant
{
    /// <inheritdoc />
    public async Task ActivateAsync(PluginLifecycleContext context, CancellationToken cancellationToken)
    {
        var result = await registrar.RegisterAsync(context.Plugin.Name, context.Directory,
            context.Manifest.McpServers, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Registered)
            throw new InvalidOperationException(result.SkippedReason ?? "Plugin MCP activation refused.");
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(PluginLifecycleContext context, CancellationToken cancellationToken)
    {
        await registrar.UnregisterAsync(context.Plugin.Name, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Reuses the plugin content trust gate rather than implementing a second hasher or policy.</summary>
public sealed class PluginMcpTrustEvaluator(PluginTrustGate gate) : IPluginTrustEvaluator
{
    /// <inheritdoc />
    public PluginTrustMode Mode => gate.Mode switch
    {
        ContentTrustMode.Disabled => PluginTrustMode.Disabled,
        ContentTrustMode.Warn => PluginTrustMode.Warn,
        ContentTrustMode.Enforce => PluginTrustMode.Enforce,
        _ => throw new InvalidOperationException("Unknown plugin content trust mode."),
    };

    /// <inheritdoc />
    public PluginTrustDecision Evaluate(string pluginName, string pluginDirectory) =>
        gate.Allow(pluginName, pluginDirectory)
            ? PluginTrustDecision.Trust
            : PluginTrustDecision.Deny("Plugin content trust verification refused activation.");
}
