using BotNexus.Gateway;
using BotNexus.Providers.Base;
using BotNexus.Core.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Tests.Unit.Tests;

public class GatewayProviderLoadingTests
{
    [Fact]
    public void AddBotNexus_LoadsOpenAiProviderFromExtensions_AndRegistersConfigKey()
    {
        var repositoryRoot = FindRepositoryRoot();
        var extensionsPath = Path.Combine(repositoryRoot, "extensions");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotNexus:ExtensionsPath"] = extensionsPath,
                ["BotNexus:Agents:Model"] = "gpt-4o",
                ["BotNexus:Providers:openai:ApiKey"] = "test-key"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBotNexus(config);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ProviderRegistry>();
        var loadedProvider = registry.GetRequired("openai");
        var workspace = provider.GetRequiredService<IAgentWorkspace>();
        var workspaceFactory = provider.GetRequiredService<IAgentWorkspaceFactory>();
        var contextBuilder = provider.GetRequiredService<IContextBuilder>();
        var contextBuilderFactory = provider.GetRequiredService<IContextBuilderFactory>();

        loadedProvider.GetType().Name.Should().Be("OpenAiProvider");
        registry.GetProviderNames().Should().Contain("openai");
        workspace.AgentName.Should().Be("default");
        workspaceFactory.Create("farnsworth").AgentName.Should().Be("farnsworth");
        contextBuilder.Should().NotBeNull();
        contextBuilderFactory.Create("farnsworth").Should().NotBeNull();
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test execution directory.");
    }
}
