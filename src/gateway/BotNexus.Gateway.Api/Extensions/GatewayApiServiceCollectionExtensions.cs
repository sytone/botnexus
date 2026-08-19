using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Filters;
using BotNexus.Gateway.Api.Logging;
using BotNexus.Gateway.Api.Triggers;
using BotNexus.Gateway.Api.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Gateway.Api.Extensions;

/// <summary>
/// DI registration for the Gateway API layer - controllers, triggers, logging.
/// Channel extensions (SignalR, etc.) are loaded dynamically by the extension loader.
/// </summary>
public static class GatewayApiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Gateway API services (controllers, triggers, logging).
    /// Call after <c>AddBotNexusGateway()</c>.
    /// </summary>
    public static IServiceCollection AddBotNexusGatewayApi(this IServiceCollection services)
    {
        // The recent-log buffer is fed by a Serilog sink (see GatewaySerilogConfiguration), not by
        // a DI ILoggerProvider: UseSerilog replaces the host ILoggerFactory, so a provider
        // registered here would never be attached and the buffer stayed empty (issue #2390).
        services.AddGatewayRecentLogStore();
        services.AddSingleton<CronTrigger>();
        services.AddSingleton<CronSessionStartupReconciler>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<CronSessionStartupReconciler>());
        services.AddSingleton<HeartbeatTrigger>();
        services.AddSingleton<SoulTrigger>();
        services.AddSingleton<IInternalTrigger>(provider => provider.GetRequiredService<CronTrigger>());
        services.AddSingleton<IInternalTrigger>(provider => provider.GetRequiredService<SoulTrigger>());
        services.AddSingleton<IInternalTrigger>(provider => provider.GetRequiredService<HeartbeatTrigger>());

        // Conversation history assembly is stateless (it only holds the conversation/session
        // stores, both singletons) so it is safe to register as a singleton. Registering it lets
        // the same assembled history view be reused by the SignalR/portal path; the controller
        // also has a constructor fallback so the endpoint works even without this registration.
        services.TryAddSingleton<IConversationHistoryAssembler, ConversationHistoryAssembler>();

        // The workspace tree cache must be a singleton or it caches nothing: the portal polls
        // GET /api/agents/{id}/workspace every ~2 minutes and each call re-walked 1000-2600 entries
        // (issue #3357). It revalidates against the filesystem on every hit, so a singleton lifetime
        // buys the saved walk without buying staleness.
        services.TryAddSingleton<WorkspaceTreeCache>();

        // Register the sparse-fieldset projection as a global result filter so every GET endpoint
        // honours ?fields=a,b,c without per-controller wiring (issue #1782). It is a no-op unless the
        // query parameter is present, keeping the default full-object response non-breaking.
        services.AddControllers(options => options.Filters.Add<SparseFieldsetResultFilter>())
            .AddApplicationPart(typeof(GatewayApiServiceCollectionExtensions).Assembly);

        return services;
    }
}
