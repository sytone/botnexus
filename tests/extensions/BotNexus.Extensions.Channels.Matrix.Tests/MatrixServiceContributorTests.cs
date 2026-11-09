using System.IO.Abstractions;
using BotNexus.Extensions.Channels.Matrix.Tests.Fakes;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.Matrix.Tests;

public sealed class MatrixServiceContributorTests
{
    [Fact]
    public void ConfigureServices_DynamicDiscovery_ResolvesDefaultsAndOneSharedAdapter()
    {
        var services = CreateDynamicStyleServices();

        DiscoverContributor().ConfigureServices(services);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        provider.GetRequiredService<IMatrixClientFactory>().ShouldBeOfType<DefaultMatrixClientFactory>();
        provider.GetRequiredService<IOptions<MatrixChannelOptions>>().Value.ShouldNotBeNull();
        var adapter = provider.GetRequiredService<MatrixChannelAdapter>();
        provider.GetServices<IChannelAdapter>().ShouldHaveSingleItem().ShouldBeSameAs(adapter);
        provider.GetServices<IConversationEventSink>().ShouldHaveSingleItem().ShouldBeSameAs(adapter);
    }

    [Fact]
    public async Task ConfigureServices_DefaultCursor_PersistsUnderRegisteredDataHomeAcrossProviders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"matrix-contributor-{Guid.NewGuid():N}");
        var dataPath = Path.Combine(root, "world-data");
        var home = new BotNexusHome(new FileSystem(), Path.Combine(root, "config"), dataPath);
        try
        {
            var services = CreateDynamicStyleServices();
            services.AddSingleton(home);
            DiscoverContributor().ConfigureServices(services);

            using (var provider = services.BuildServiceProvider())
            {
                var cursor = provider.GetRequiredService<IMatrixSyncCursorStore>();
                cursor.ShouldBeOfType<SqliteMatrixSyncCursorStore>();
                cursor.ShouldBeSameAs(provider.GetRequiredService<IMatrixSyncCursorStore>());
                await cursor.SetAsync("agent", "account", "batch-42");
            }

            File.Exists(Path.Combine(dataPath, "data", "matrix-sync-cursor.sqlite")).ShouldBeTrue();
            Directory.Exists(home.RootPath).ShouldBeFalse();
            using var reopened = services.BuildServiceProvider();
            (await reopened.GetRequiredService<IMatrixSyncCursorStore>().GetAsync("agent", "account"))
                .ShouldBe("batch-42");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConfigureServices_CustomServicesAndRepeatedContribution_PreservesOverridesWithoutDuplicates()
    {
        var services = CreateDynamicStyleServices();
        var factory = new FakeMatrixClientFactory();
        var cursor = new FakeMatrixSyncCursorStore();
        services.AddSingleton<IMatrixClientFactory>(factory);
        services.AddSingleton<IMatrixSyncCursorStore>(cursor);
        var contributor = DiscoverContributor();

        contributor.ConfigureServices(services);
        contributor.ConfigureServices(services);

        using var provider = services.BuildServiceProvider();
        provider.GetServices<IMatrixClientFactory>().ShouldHaveSingleItem().ShouldBeSameAs(factory);
        provider.GetServices<IMatrixSyncCursorStore>().ShouldHaveSingleItem().ShouldBeSameAs(cursor);
        var adapter = provider.GetRequiredService<MatrixChannelAdapter>();
        provider.GetServices<IChannelAdapter>().ShouldHaveSingleItem().ShouldBeSameAs(adapter);
        provider.GetServices<IConversationEventSink>().ShouldHaveSingleItem().ShouldBeSameAs(adapter);
    }

    private static IServiceContributor DiscoverContributor()
    {
        // Mirror the loader's assembly scan and parameterless activation, without statically
        // referencing the contributor: the missing production registration must fail at runtime.
        var type = typeof(MatrixChannelAdapter).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && !type.IsGenericTypeDefinition
                && typeof(IServiceContributor).IsAssignableFrom(type))
            .ShouldHaveSingleItem();
        return Activator.CreateInstance(type) as IServiceContributor
            ?? throw new InvalidOperationException("Matrix contributor must support parameterless activation.");
    }

    private static ServiceCollection CreateDynamicStyleServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // RegisterServices groups the adapter's two discovered contracts and aliases them to
        // one concrete singleton BEFORE InvokeServiceContributors. Do not call the static helper.
        services.AddSingleton<MatrixChannelAdapter>();
        services.AddSingleton<IChannelAdapter>(provider => provider.GetRequiredService<MatrixChannelAdapter>());
        services.AddSingleton<IConversationEventSink>(provider => provider.GetRequiredService<MatrixChannelAdapter>());
        return services;
    }
}
