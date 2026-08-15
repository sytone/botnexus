using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace BotNexus.Extensions.GitHub.Tests;

/// <summary>
/// AC1 (#2732): the extension registers through the existing <c>IServiceContributor</c> seam, and the
/// registration yields a working, cached credential provider.
/// </summary>
public sealed class GitHubServiceContributorTests
{
    [Fact]
    public void Contributor_ImplementsTheServiceContributorSeam()
    {
        typeof(BotNexus.Gateway.Abstractions.Extensions.IServiceContributor)
            .IsAssignableFrom(typeof(GitHubServiceContributor))
            .ShouldBeTrue();
    }

    [Fact]
    public void Contributor_HasPublicParameterlessConstructor_AsTheLoaderRequires()
    {
        // AssemblyLoadContextExtensionLoader does Activator.CreateInstance(type) — a contributor with
        // only a parameterised constructor is caught, logged, and silently skipped at startup.
        typeof(GitHubServiceContributor)
            .GetConstructor(Type.EmptyTypes)
            .ShouldNotBeNull();
    }

    [Fact]
    public void ConfigureServices_RegistersACredentialProviderResolvableFromTheContainer()
    {
        var services = new ServiceCollection();

        new GitHubServiceContributor().ConfigureServices(services);

        using var provider = services.BuildServiceProvider();
        var credential = provider.GetService<IGitHubCredentialProvider>();

        credential.ShouldNotBeNull();
        credential.ShouldBeOfType<CachedGitHubCredentialProvider>();
    }

    [Fact]
    public void ConfigureServices_RegistersTheProviderAsASingleton_SoOneCacheServesEveryCaller()
    {
        var services = new ServiceCollection();
        new GitHubServiceContributor().ConfigureServices(services);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IGitHubCredentialProvider>()
            .ShouldBeSameAs(provider.GetRequiredService<IGitHubCredentialProvider>());
    }

    [Fact]
    public void ConfigureServices_WithNullServices_Throws() =>
        Should.Throw<ArgumentNullException>(() => new GitHubServiceContributor().ConfigureServices(null!));

    [Fact]
    public void ConfigureServices_DoesNotOverrideAnAlreadyRegisteredProvider()
    {
        // TryAdd semantics: a host that has deliberately substituted a provider keeps it.
        var services = new ServiceCollection();
        var substitute = new SubstituteCredentialProvider();
        services.AddSingleton<IGitHubCredentialProvider>(substitute);

        new GitHubServiceContributor().ConfigureServices(services);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IGitHubCredentialProvider>().ShouldBeSameAs(substitute);
    }

    private sealed class SubstituteCredentialProvider : IGitHubCredentialProvider
    {
        public Task AuthenticateAsync(HttpRequestMessage request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
