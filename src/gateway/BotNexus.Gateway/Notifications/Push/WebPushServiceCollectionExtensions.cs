using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>
/// DI registration for web push delivery.
/// </summary>
public static class WebPushServiceCollectionExtensions
{
    /// <summary>
    /// Registers the subscription store, the VAPID identity, the sender and the bridge.
    /// </summary>
    /// <remarks>
    /// Registered unconditionally. With no subscriptions the sender does nothing on each
    /// notification and the bridge idles, which costs nothing and means a device can subscribe at
    /// any time without a restart - whereas a flag would have to be found and set first, by
    /// someone who does not yet know the feature exists.
    /// </remarks>
    /// <param name="services">Service collection.</param>
    /// <param name="dbPath">Path to the subscriptions database.</param>
    /// <param name="vapidPath">Path to the VAPID key file.</param>
    /// <param name="subject">Operator contact for push services: a mailto: or https: URI.</param>
    /// <param name="fileSystem">Filesystem abstraction; resolved from DI when omitted.</param>
    public static IServiceCollection AddBotNexusWebPush(
        this IServiceCollection services,
        string dbPath,
        string vapidPath,
        string subject,
        IFileSystem? fileSystem = null)
    {
        services.TryAddSingleton<IPushSubscriptionStore>(sp =>
            new SqlitePushSubscriptionStore(
                dbPath,
                fileSystem ?? sp.GetService<IFileSystem>(),
                sp.GetService<TimeProvider>()));

        services.TryAddSingleton(new VapidKeyStore(vapidPath, subject));

        // A named client so the push-service timeout is short: a slow push service must not hold
        // the bridge up behind it while other subscribers wait.
        services.AddHttpClient<WebPushSender>(client => client.Timeout = TimeSpan.FromSeconds(15));

        services.AddHostedService<WebPushBridge>();

        return services;
    }

    /// <summary>
    /// Registers the iOS device store, the APNs sender and its bridge.
    /// </summary>
    /// <remarks>
    /// Registered whether or not APNs is configured, and inert when it is not. A gateway with no
    /// Apple Developer account is the ordinary case rather than a misconfiguration, so the bridge
    /// says once that it is idle and then does nothing - it does not warn, and it does not have to
    /// be switched on later by someone who has to know it exists first.
    /// </remarks>
    /// <param name="services">Service collection.</param>
    /// <param name="dbPath">Path to the device database.</param>
    /// <param name="options">Apple credentials, from configuration.</param>
    /// <param name="fileSystem">Filesystem abstraction; resolved from DI when omitted.</param>
    public static IServiceCollection AddBotNexusApns(
        this IServiceCollection services,
        string dbPath,
        ApnsOptions options,
        IFileSystem? fileSystem = null)
    {
        services.TryAddSingleton<IApnsDeviceStore>(sp =>
            new SqliteApnsDeviceStore(
                dbPath,
                fileSystem ?? sp.GetService<IFileSystem>(),
                sp.GetService<TimeProvider>()));

        services.TryAddSingleton(options);
        services.TryAddSingleton(sp => new ApnsTokenProvider(options, sp.GetService<TimeProvider>()));

        services.AddHttpClient<ApnsSender>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);

            // APNs is HTTP/2 only and will not negotiate down.
            client.DefaultRequestVersion = System.Net.HttpVersion.Version20;
            client.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact;
        });

        services.AddHostedService<ApnsBridge>();

        return services;
    }
}
