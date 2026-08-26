using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Contracts.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.TaskNexus;

/// <summary>
/// Registers <see cref="TaskNexusWebhookTargetNotifier"/> as an agent webhook delivery target
/// through the existing <see cref="IServiceContributor"/> seam.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="IServiceContributor"/> seam is used rather than adding
/// <see cref="IAgentWebhookTargetNotifier"/> to the loader's discoverable-contract allow-list.
/// The seam is invoked for every loaded extension regardless of declared extension type, so this
/// keeps a downstream-product concern entirely out of the core loader.
/// </para>
/// <para>
/// Registration is unconditional and cheap: nothing contacts TaskNexus until the provisioner
/// pushes a binding, and with no <c>baseUrl</c> configured the notifier returns without touching
/// its <see cref="HttpClient"/>. An unconfigured host therefore makes zero outbound attempts.
/// </para>
/// <para>
/// It is registered with <c>AddSingleton</c> rather than <c>TryAddSingleton</c> because
/// <see cref="IAgentWebhookTargetNotifier"/> is consumed as a SET
/// (<c>IEnumerable&lt;IAgentWebhookTargetNotifier&gt;</c>). A <c>TryAdd</c> would make a second
/// delivery target silently suppress this one instead of running alongside it.
/// </para>
/// </remarks>
public sealed class TaskNexusServiceContributor : IServiceContributor
{
    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAgentWebhookTargetNotifier>(provider => new TaskNexusWebhookTargetNotifier(
            provider.GetService<IHttpClientFactory>()?.CreateClient("botnexus-tasknexus") ?? new HttpClient(),
            provider.GetService<IConfiguration>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<TaskNexusWebhookTargetNotifier>()));
    }
}
