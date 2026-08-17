using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// Registers the parts of the test channel that contract-based auto-discovery cannot express.
/// </summary>
/// <remarks>
/// <para>
/// The extension loader discovers <c>IChannelAdapter</c> and <c>IEndpointContributor</c> by
/// contract, but it has no way to know the adapter needs bound <c>IOptions&lt;TestChannelOptions&gt;</c>,
/// nor that the log-capture buffer and its <c>ILoggerProvider</c> must exist for the
/// <c>/test-channel/logs</c> endpoints to work. Without this contributor the dynamically-loaded
/// extension would come up with endpoints that silently 404 — the failure mode this project exists
/// to eliminate, reproduced in the harness itself.
/// </para>
/// <para>
/// It runs only when the extension is loaded, which the disabled manifest already prevents in any
/// configuration that has not deliberately opted in.
/// </para>
/// <para>
/// It registers SUPPORT services only. Registering the adapter or the endpoint contributor here
/// would duplicate what the loader already registers by contract, producing two adapter instances
/// for one channel key — of which only one would ever be started.
/// </para>
/// </remarks>
public sealed class TestChannelServiceContributor : IServiceContributor
{
    /// <inheritdoc/>
    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddBotNexusTestChannelSupport();
    }
}
