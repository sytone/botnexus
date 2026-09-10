using BotNexus.Gateway.Abstractions.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Notifications;

/// <summary>
/// DI registration for the notification store.
/// </summary>
public static class NotificationStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="INotificationStore"/> backed by SQLite at <paramref name="dbPath"/>.
    /// </summary>
    /// <remarks>
    /// Server-side and in the same writable data directory as the other stores, so what an agent
    /// did while nobody was watching is readable from whatever device the operator picks up next -
    /// which is the point of the feature rather than an incidental detail of where it is stored.
    /// </remarks>
    /// <param name="services">Service collection.</param>
    /// <param name="dbPath">Path to the notifications database.</param>
    /// <param name="fileSystem">Filesystem abstraction; resolved from DI when omitted.</param>
    public static IServiceCollection AddBotNexusNotifications(
        this IServiceCollection services,
        string dbPath,
        IFileSystem? fileSystem = null)
    {
        services.TryAddSingleton<INotificationStore>(sp =>
            new SqliteNotificationStore(
                dbPath,
                fileSystem ?? sp.GetService<IFileSystem>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<ILogger<SqliteNotificationStore>>()));

        // Advisory publisher over that store. Registered here so a caller raising a notification
        // never has to know where notifications are kept.
        services.TryAddSingleton<INotificationBroadcaster, InMemoryNotificationBroadcaster>();

        services.TryAddSingleton<INotificationPublisher>(sp =>
            new NotificationPublisher(
                sp.GetRequiredService<INotificationStore>(),
                sp.GetService<INotificationBroadcaster>(),
                sp.GetService<ILogger<NotificationPublisher>>()));

        return services;
    }
}
