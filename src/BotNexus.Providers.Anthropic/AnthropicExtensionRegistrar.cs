using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Providers.Anthropic;

/// <summary>Registers Anthropic provider services when loaded as an extension.</summary>
public sealed class AnthropicExtensionRegistrar : IExtensionRegistrar
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ILlmProvider>(sp =>
        {
            var botConfig = sp.GetRequiredService<IOptions<BotNexusConfig>>().Value;
            var providerConfig = configuration.Get<ProviderConfig>() ?? new ProviderConfig();
            var logger = sp.GetRequiredService<ILogger<AnthropicProvider>>();

            return new AnthropicProvider(
                apiKey: providerConfig.ApiKey,
                model: providerConfig.DefaultModel ?? botConfig.Agents.Model,
                apiBase: providerConfig.ApiBase,
                logger: logger,
                maxRetries: providerConfig.MaxRetries);
        });
    }
}
