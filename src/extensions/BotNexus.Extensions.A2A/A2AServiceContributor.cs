using BotNexus.Gateway.Abstractions.A2A;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.A2A;

/// <summary>Registers the generic contributor while leaving concrete service profiles to independent extensions.</summary>
public sealed class A2AServiceContributor : IServiceContributor
{
    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAgentToolContributor>(provider =>
            new A2AToolContributor(
                provider.GetServices<IA2AServiceProfile>(),
                static () => new BotNexus.Gateway.A2A.A2AClient()));
    }
}
