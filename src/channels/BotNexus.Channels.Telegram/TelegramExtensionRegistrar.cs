using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Channels.Telegram;

public sealed class TelegramExtensionRegistrar : IExtensionRegistrar
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        var channelConfig = configuration.Get<ChannelConfig>() ?? new ChannelConfig();
        if (!channelConfig.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(channelConfig.BotToken))
            throw new InvalidOperationException("Telegram channel is enabled but BotToken is missing.");

        services.AddSingleton<IChannel>(sp => new TelegramChannel(
            channelConfig.BotToken,
            sp.GetRequiredService<IMessageBus>(),
            sp.GetRequiredService<ILogger<TelegramChannel>>(),
            channelConfig.AllowFrom));
    }
}
