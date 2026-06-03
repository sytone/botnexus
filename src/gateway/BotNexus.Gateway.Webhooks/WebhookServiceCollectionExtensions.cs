using Microsoft.Extensions.DependencyInjection;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// DI registration for the webhook subsystem stores.
/// </summary>
public static class WebhookServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IWebhookRegistrationStore"/> and <see cref="IWebhookRunStore"/>
    /// backed by SQLite at <paramref name="dbPath"/>.
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
                sp.GetService<Microsoft.Extensions.Logging.ILogger<SqliteWebhookRegistrationStore>>()));

        services.AddSingleton<IWebhookRunStore>(sp =>
            new SqliteWebhookRunStore(
                dbPath,
                fileSystem ?? sp.GetService<IFileSystem>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<SqliteWebhookRunStore>>()));

        return services;
    }
}
