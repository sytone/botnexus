using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Channels.Slack;

public sealed class SlackExtensionRegistrar : IExtensionRegistrar
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        var channelConfig = configuration.Get<ChannelConfig>() ?? new ChannelConfig();
        if (!channelConfig.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(channelConfig.BotToken))
            throw new InvalidOperationException("Slack channel is enabled but BotToken is missing.");
        if (string.IsNullOrWhiteSpace(channelConfig.SigningSecret))
            throw new InvalidOperationException("Slack channel is enabled but SigningSecret is missing.");

        services.AddSingleton<IChannel>(sp => new SlackChannel(
            channelConfig.BotToken,
            sp.GetRequiredService<IMessageBus>(),
            sp.GetRequiredService<ILogger<SlackChannel>>(),
            channelConfig.AllowFrom));
        services.AddSingleton<IWebhookHandler>(sp => new SlackWebhookHandler(
            channelConfig.SigningSecret,
            sp.GetRequiredService<IMessageBus>(),
            sp.GetRequiredService<ILogger<SlackWebhookHandler>>()));
    }
}
