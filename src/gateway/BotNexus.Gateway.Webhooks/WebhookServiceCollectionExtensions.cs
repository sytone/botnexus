using Microsoft.Extensions.Configuration;
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
    /// Configuration section (relative to the gateway root) that binds
    /// <see cref="WebhookConversationRetentionOptions"/>.
    /// </summary>
    public const string ConversationRetentionSection = "gateway:webhooks:conversationRetention";

    /// <summary>
    /// Registers <see cref="IWebhookRegistrationStore"/> and <see cref="IWebhookRunStore"/>
    /// backed by SQLite at <paramref name="dbPath"/>. Also registers the
    /// <see cref="WebhookRunRetentionHostedService"/> for periodic purge of old runs and the
    /// <see cref="WebhookConversationRetentionHostedService"/> for the webhook-specific
    /// conversation retention policy (issue #2125).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="dbPath">Path to the webhook SQLite database.</param>
    /// <param name="fileSystem">Optional filesystem abstraction for testability.</param>
    /// <param name="configuration">
    /// Optional configuration root. When supplied, <see cref="WebhookConversationRetentionOptions"/>
    /// is bound from <see cref="ConversationRetentionSection"/>.
    /// </param>
    public static IServiceCollection AddBotNexusWebhooks(
        this IServiceCollection services,
        string dbPath,
        IFileSystem? fileSystem = null,
        IConfiguration? configuration = null)
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

        services.AddOptions<WebhookConversationRetentionOptions>();
        if (configuration is not null)
            services.Configure<WebhookConversationRetentionOptions>(
                configuration.GetSection(ConversationRetentionSection).Bind);
        services.AddHostedService<WebhookConversationRetentionHostedService>();

        return services;
    }
}
