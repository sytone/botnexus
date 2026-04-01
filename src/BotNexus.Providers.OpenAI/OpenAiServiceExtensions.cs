using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Providers.OpenAI;

/// <summary>Extension methods for registering the OpenAI provider.</summary>
public static class OpenAiServiceExtensions
{
    /// <summary>Registers the OpenAI provider.</summary>
    public static IServiceCollection AddOpenAiProvider(this IServiceCollection services)
    {
        services.AddSingleton<ILlmProvider>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<BotNexusConfig>>().Value;
            if (!config.Providers.TryGetValue("openai", out var providerConfig))
            {
                throw new InvalidOperationException("Provider configuration for 'openai' was not found.");
            }

            var logger = sp.GetRequiredService<ILogger<OpenAiProvider>>();

            return new OpenAiProvider(
                apiKey: providerConfig.ApiKey,
                model: providerConfig.DefaultModel ?? config.Agents.Model,
                apiBase: providerConfig.ApiBase,
                logger: logger,
                maxRetries: providerConfig.MaxRetries);
        });
        return services;
    }
}
