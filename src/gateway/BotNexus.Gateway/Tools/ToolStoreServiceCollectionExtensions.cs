using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// DI registration for the portal Tools store.
/// </summary>
public static class ToolStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IToolStore"/> backed by SQLite at <paramref name="dbPath"/>.
    /// The database file persists user-defined portal tools across gateway restarts.
    /// </summary>
    public static IServiceCollection AddBotNexusTools(
        this IServiceCollection services,
        string dbPath,
        IFileSystem? fileSystem = null)
    {
        services.TryAddSingleton<IToolStore>(sp =>
            new SqliteToolStore(
                dbPath,
                fileSystem ?? sp.GetService<IFileSystem>(),
                sp.GetService<ILogger<SqliteToolStore>>()));

        return services;
    }
}
