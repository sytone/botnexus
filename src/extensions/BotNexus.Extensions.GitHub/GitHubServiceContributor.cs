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

        services.TryAddSingleton<IGitHubIdentityResolver>(provider =>
        {
            var options = provider.GetRequiredService<GitHubCredentialOptions>();
            var httpFactory = provider.GetService<IHttpClientFactory>();
            var timeProvider = provider.GetService<TimeProvider>() ?? TimeProvider.System;
            var loggerFactory = provider.GetService<ILoggerFactory>();

            // One token source and one cache PER IDENTITY. This is what makes two agents on
            // different profiles independent in a single process (#2733 AC4): there is no shared
            // mutable identity for one agent's activity to change out from under another's.
            return new ConfiguredGitHubIdentityResolver(options, identity => new CachedGitHubCredentialProvider(
                new HttpGitHubInstallationTokenSource(
                    httpFactory?.CreateClient("botnexus-github") ?? new HttpClient(),
                    options,
                    timeProvider,
                    identity),
                timeProvider,
                TimeSpan.FromSeconds(Math.Max(0, options.ExpirySkewSeconds)),
                loggerFactory?.CreateLogger<CachedGitHubCredentialProvider>()));
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
            var resolver = provider.GetRequiredService<IGitHubIdentityResolver>();
            var fallback = provider.GetRequiredService<IGitHubCredentialProvider>();
            var options = provider.GetRequiredService<GitHubCredentialOptions>();
            var httpFactory = provider.GetService<IHttpClientFactory>();

            return new GitHubToolsContributor((GitHubToolsConfig _, BotNexus.Domain.Primitives.AgentId agentId) => new HttpGitHubApiClient(
                httpFactory?.CreateClient("botnexus-github") ?? new HttpClient(),
                // A host with no per-agent identity map keeps the single platform identity it had
                // before #2733; a host that HAS one gets strict per-agent resolution that fails
                // closed. Silently downgrading a configured host to the shared identity would
                // re-introduce exactly the cross-agent authorship bug this issue closes.
                options.AgentIdentities.Count == 0 && options.Identities.Count == 0
                    ? fallback
                    : new AgentScopedGitHubCredentialProvider(resolver, agentId),
                options));
        });
    }
}
