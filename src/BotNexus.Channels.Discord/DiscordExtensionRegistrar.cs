using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Channels.Discord;

public sealed class DiscordExtensionRegistrar : IExtensionRegistrar
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        var channelConfig = configuration.Get<ChannelConfig>() ?? new ChannelConfig();
        if (!channelConfig.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(channelConfig.BotToken))
            throw new InvalidOperationException("Discord channel is enabled but BotToken is missing.");

        services.AddSingleton<IChannel>(sp => new DiscordChannel(
            channelConfig.BotToken,
            sp.GetRequiredService<IMessageBus>(),
            sp.GetRequiredService<ILogger<DiscordChannel>>(),
            channelConfig.AllowFrom));
    }
}
