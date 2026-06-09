using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Agents;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Gateway.Hooks;

/// <summary>
/// Registers built-in hook handlers on the shared <see cref="HookDispatcher"/> singleton
/// during application startup.
/// </summary>
/// <remarks>
/// <para>
/// Built-in handlers require DI-resolved services (e.g. <see cref="ToolPolicyHookHandler"/>
/// depends on <see cref="Security.DefaultToolPolicyProvider"/>). These services are not available
/// at service-collection time when extensions are loaded. This hosted service bridges that gap
/// by registering the handlers once the service provider is fully built.
/// </para>
/// <para>
/// Extension-discovered handlers are registered by <see cref="Extensions.AssemblyLoadContextExtensionLoader"/>
/// during <c>LoadConfiguredExtensionsAsync</c> (before the host starts). Built-in handlers
/// registered here augment the same dispatcher instance.
/// </para>
/// </remarks>
public sealed class HookDispatcherInitializer : IHostedService
{
    private readonly IHookDispatcher _hookDispatcher;
    private readonly ToolPolicyHookHandler _toolPolicyHandler;
    private readonly AgentsMdPromptHookHandler _agentsMdHandler;

    public HookDispatcherInitializer(
        IHookDispatcher hookDispatcher,
        ToolPolicyHookHandler toolPolicyHandler,
        AgentsMdPromptHookHandler agentsMdHandler)
    {
        _hookDispatcher = hookDispatcher;
        _toolPolicyHandler = toolPolicyHandler;
        _agentsMdHandler = agentsMdHandler;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _hookDispatcher.Register<BeforeToolCallEvent, BeforeToolCallResult>(_toolPolicyHandler);
        _hookDispatcher.Register<BeforePromptBuildEvent, BeforePromptBuildResult>(_agentsMdHandler);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
