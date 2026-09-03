using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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

        // #3807: the inbound route is anonymous and must read the body before it can verify the
        // HMAC, so the pre-auth read needs its own byte ceiling and in-flight cap. Singleton
        // because the concurrency cap is only meaningful when every request shares one semaphore.
        services.TryAddSingleton(_ => new WebhookInboundBodyGuard());

        services.AddHostedService<WebhookRunRetentionHostedService>();

        services.AddOptions<WebhookConversationRetentionOptions>();
        if (configuration is not null)
            services.Configure<WebhookConversationRetentionOptions>(
                configuration.GetSection(ConversationRetentionSection).Bind);
        services.AddHostedService<WebhookConversationRetentionHostedService>();

        // #3523: reconcile per-agent outbound webhook registrations from agent lifecycle. Exposed
        // BOTH as IHostedService (startup reconciliation over the whole registry) and as
        // IAgentWebhookProvisioner (per-agent calls from AgentsController), resolving to the same
        // singleton - the DI idiom CronServiceCollectionExtensions uses for the cron provisioners.
        services.AddSingleton<AgentWebhookProvisioner>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AgentWebhookProvisioner>());
        services.TryAddSingleton<IAgentWebhookProvisioner>(sp => sp.GetRequiredService<AgentWebhookProvisioner>());

        return services;
    }
}
