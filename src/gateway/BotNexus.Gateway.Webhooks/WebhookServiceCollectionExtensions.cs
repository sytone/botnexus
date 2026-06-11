using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// DI registration for the webhook subsystem stores.
/// </summary>
public static class WebhookServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IWebhookRegistrationStore"/> and <see cref="IWebhookRunStore"/>
    /// backed by SQLite at <paramref name="dbPath"/>. Also registers the
    /// <see cref="WebhookRunRetentionHostedService"/> for periodic purge of old runs.
    /// </summary>
    public static IServiceCollection AddBotNexusWebhooks(
        this IServiceCollection services,
        string dbPath,
        IFileSystem? fileSystem = null)
    {
        services.AddSingleton<IWebhookRegistrationStore>(sp =>
            new SqliteWebhookRegistrationStore(
                dbPath,
                fileSystem ?? sp.GetService<IFileSystem>(),
                sp.GetService<ILogger<SqliteWebhookRegistrationStore>>()));

        services.AddSingleton<IWebhookRunStore>(sp =>
            new SqliteWebhookRunStore(
                dbPath,
                fileSystem ?? sp.GetService<IFileSystem>(),
                sp.GetService<ILogger<SqliteWebhookRunStore>>()));

        services.AddHostedService<WebhookRunRetentionHostedService>();

        return services;
    }
}
