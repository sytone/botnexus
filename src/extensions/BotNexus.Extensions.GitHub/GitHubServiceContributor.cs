using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// Registers the platform-owned GitHub credential (#2732) through the existing
/// <see cref="IServiceContributor"/> seam, so the provider is a host-container singleton rather than
/// anything an agent can construct or reach.
/// </summary>
/// <remarks>
/// Registration is unconditional and cheap: nothing contacts GitHub until a caller asks the provider
/// to authenticate a request, so an unconfigured host pays no cost and fails only at first use with
/// a <see cref="GitHubCredentialException"/> naming the missing setting.
/// </remarks>
public sealed class GitHubServiceContributor : IServiceContributor
{
    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(provider =>
        {
            var options = new GitHubCredentialOptions();
            provider.GetService<IConfiguration>()?
                .GetSection(GitHubCredentialOptions.SectionName)
                .Bind(options);
            return options;
        });

        services.TryAddSingleton<IGitHubInstallationTokenSource>(provider => new HttpGitHubInstallationTokenSource(
            provider.GetService<IHttpClientFactory>()?.CreateClient("botnexus-github") ?? new HttpClient(),
            provider.GetRequiredService<GitHubCredentialOptions>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System));

        services.TryAddSingleton<IGitHubCredentialProvider>(provider =>
        {
            var options = provider.GetRequiredService<GitHubCredentialOptions>();
            return new CachedGitHubCredentialProvider(
                provider.GetRequiredService<IGitHubInstallationTokenSource>(),
                provider.GetService<TimeProvider>() ?? TimeProvider.System,
                TimeSpan.FromSeconds(Math.Max(0, options.ExpirySkewSeconds)),
                provider.GetService<ILoggerFactory>()?.CreateLogger<CachedGitHubCredentialProvider>());
        });

        // The agent-facing tool surface (#2627). Registered as an IAgentToolContributor so tools are
        // materialised per agent from that agent's configuration - which is what keeps the acting
        // identity a configuration decision rather than a tool argument.
        services.AddSingleton<IAgentToolContributor>(provider =>
        {
            var credentials = provider.GetRequiredService<IGitHubCredentialProvider>();
            var options = provider.GetRequiredService<GitHubCredentialOptions>();
            var httpFactory = provider.GetService<IHttpClientFactory>();

            return new GitHubToolsContributor(_ => new HttpGitHubApiClient(
                httpFactory?.CreateClient("botnexus-github") ?? new HttpClient(),
                credentials,
                options));
        });
    }
}
