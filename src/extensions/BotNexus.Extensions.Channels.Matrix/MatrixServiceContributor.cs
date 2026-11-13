using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using BotNexus.Persistence.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BotNexus.Extensions.Channels.Matrix;

/// <summary>
/// Registers Matrix support services for dynamically loaded hosts, which do not call
/// <see cref="MatrixServiceCollectionExtensions.AddBotNexusMatrixChannel"/>.
/// </summary>
/// <remarks>
/// The loader already registers the adapter's concrete singleton and aliases both
/// IChannelAdapter and IConversationEventSink to it before invoking this contributor.
/// Register support services only: calling the static channel helper here would duplicate
/// those contract registrations. TryAdd preserves host-supplied factories and cursor stores.
/// </remarks>
public sealed class MatrixServiceContributor : IServiceContributor
{
    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<MatrixChannelOptions>();
        services.AddHttpClient();
        services.TryAddSingleton<IMatrixClientFactory>(provider => new DefaultMatrixClientFactory(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetService<ISecretRedactor>()));

        services.TryAddSingleton<IMatrixSyncCursorStore>(provider =>
        {
            // Match the static helper's owned-store policy: prefer the host's verified home,
            // including a separate world data root, rather than deriving an extension-local home.
            var home = provider.GetService<BotNexusHome>();
            var dataRoot = home?.DataPath ?? BotNexusHome.ResolveDataPath() ?? BotNexusHome.ResolveHomePath();
            return new SqliteMatrixSyncCursorStore(
                SqliteStorePathPolicy.ResolveOwnedStorePath(
                    Path.Combine(dataRoot, "data"),
                    "matrix-sync-cursor"));
        });
    }
}
