using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Nav;

/// <summary>
/// DI registration for the portal nav-order store.
/// </summary>
public static class NavOrderStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="INavOrderStore"/> backed by SQLite at <paramref name="dbPath"/>.
    /// The database file persists per-user nav-order overrides across gateway restarts.
    /// </summary>
    public static IServiceCollection AddBotNexusNavOrder(
        this IServiceCollection services,
        string dbPath,
        IFileSystem? fileSystem = null)
    {
        services.TryAddSingleton<INavOrderStore>(sp =>
            new SqliteNavOrderStore(
                dbPath,
                fileSystem ?? sp.GetService<IFileSystem>(),
                sp.GetService<ILogger<SqliteNavOrderStore>>()));

        return services;
    }
}
