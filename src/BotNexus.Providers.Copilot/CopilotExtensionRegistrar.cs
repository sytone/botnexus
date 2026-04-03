using BotNexus.Core.Abstractions;
using BotNexus.Core.Bus;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Providers.Copilot;

public sealed class CopilotExtensionRegistrar : IExtensionRegistrar
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        var providerConfig = configuration.Get<CopilotConfig>() ?? new CopilotConfig();
        services.AddSingleton(providerConfig);
        services.TryAddSingleton<IOAuthTokenStore, FileOAuthTokenStore>();
        services.AddSingleton<GitHubDeviceCodeFlow>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<GitHubDeviceCodeFlow>>();
            var activityStream = sp.GetRequiredService<IActivityStream>();
            var messageStore = sp.GetRequiredService<SystemMessageStore>();
            return new GitHubDeviceCodeFlow(new HttpClient(), logger, activityStream, messageStore);
        });

        services.AddSingleton<ILlmProvider>(sp =>
        {
            var botConfig = sp.GetRequiredService<IOptions<BotNexusConfig>>().Value;
            var config = sp.GetRequiredService<CopilotConfig>();
            if (string.IsNullOrWhiteSpace(config.DefaultModel))
                config.DefaultModel = botConfig.Agents.Model;

            var logger = sp.GetRequiredService<ILogger<CopilotProvider>>();
            var tokenStore = sp.GetRequiredService<IOAuthTokenStore>();
            var deviceCodeFlow = sp.GetRequiredService<GitHubDeviceCodeFlow>();

            return new CopilotProvider(config, tokenStore, deviceCodeFlow, logger);
        });
    }
}
