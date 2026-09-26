using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Provides explicit reads from one concrete extension configuration scope at a time.
/// Callers that need fallback semantics read each relevant scope and apply policy locally.
/// </summary>
public static class ExtensionConfigScopeReader
{
    /// <summary>Binds an extension entry stored at <c>world.extensions</c> only.</summary>
    public static T? BindWorldExtension<T>(this PlatformConfig config, string extensionId) where T : class
        => ExtensionConfigBinder.Bind<T>(config.World?.Extensions, extensionId);

    /// <summary>Binds an extension entry stored at <c>gateway.extensions</c> only.</summary>
    public static T? BindGatewayExtension<T>(this PlatformConfig config, string extensionId) where T : class
        => ExtensionConfigBinder.Bind<T>(config.Gateway?.Extensions, extensionId);

    /// <summary>Binds an extension entry stored at <c>agents.defaults.extensions</c> only.</summary>
    public static T? BindAgentDefaultExtension<T>(this PlatformConfig config, string extensionId) where T : class
        => ExtensionConfigBinder.Bind<T>(config.AgentDefaults?.Extensions, extensionId);

    /// <summary>Binds an extension entry stored for one named agent only.</summary>
    public static T? BindAgentExtension<T>(this PlatformConfig config, AgentId agentId, string extensionId) where T : class
    {
        if (config.Agents is null || !config.Agents.TryGetValue(agentId.Value, out var agent))
            return null;

        return ExtensionConfigBinder.Bind<T>(agent.Extensions, extensionId);
    }
}
