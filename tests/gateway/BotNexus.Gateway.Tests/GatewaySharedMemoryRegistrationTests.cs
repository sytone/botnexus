using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using BotNexus.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Shared memory is wired to something (#3232).
///
/// <remarks>
/// The registry, its per-store reader/writer ACLs, the promoter and the trust tiering were all
/// built and thoroughly unit-tested against mocks - and registered NOWHERE. Every consumer takes
/// <see cref="ISharedMemoryStoreRegistry"/> as optional and silently does nothing when it is
/// null, so "silently does nothing" was the only behaviour the feature had ever had in
/// production. Nothing was red, because nothing asked whether the container could produce one.
/// <para>
/// That is what this file asks. It is a registration test, not a behaviour test: the behaviour
/// already has plenty of coverage, and had it the whole time.
/// </para>
/// </remarks>
/// </summary>
public sealed class GatewaySharedMemoryRegistrationTests
{
    private static ServiceProvider Build(IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBotNexusGateway(configuration);
        services.AddSingleton<IFileSystem>(new MockFileSystem());
        services.AddSingleton(new BotNexusHome(Path.Combine(Path.GetTempPath(), "shared-memory-registration")));
        if (configuration is not null)
            services.AddSingleton(configuration);
        return services.BuildServiceProvider();
    }

    private static IConfiguration ConfigWith(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void The_container_can_actually_produce_a_shared_memory_registry()
    {
        // THE test. Everything else about shared memory already passed while this did not hold.
        using var provider = Build();

        provider.GetService<ISharedMemoryStoreRegistry>()
            .ShouldNotBeNull("every consumer resolves this optionally, so an unregistered registry is silent");
    }

    [Fact]
    public void With_nothing_configured_no_agent_can_read_or_write_anything()
    {
        // Registering the registry must not, by itself, turn any sharing on. An empty registry
        // answers no to everything, which is the private-memory-only behaviour that shipped.
        using var provider = Build();
        var registry = provider.GetRequiredService<ISharedMemoryStoreRegistry>();

        registry.GetAllConfigs().ShouldBeEmpty();
        registry.GetReadableStores("gantry-manager").ShouldBeEmpty();
        registry.GetWritableStores("gantry-manager").ShouldBeEmpty();
    }

    [Fact]
    public void Disposing_the_container_synchronously_does_not_throw()
    {
        // ISharedMemoryStoreRegistry is IAsyncDisposable only, and a ServiceProvider disposed with
        // Dispose() THROWS on an async-only disposable rather than falling back. Registering the
        // registry without a synchronous path would therefore turn a synchronous shutdown into an
        // InvalidOperationException - found by this file, on its first run.
        var provider = Build(ConfigWith(("gateway:memory:sharedStores:0:name", "s")));
        provider.GetRequiredService<ISharedMemoryStoreRegistry>();

        Should.NotThrow(() => provider.Dispose());
    }

    [Fact]
    public void A_configured_store_reaches_the_registry_with_its_access_list_intact()
    {
        // The whole path: config.json -> options -> mapping -> registry.
        using var provider = Build(ConfigWith(
            ("gateway:memory:sharedStores:0:name", "platform-knowledge"),
            ("gateway:memory:sharedStores:0:description", "What we have learned about the platform"),
            ("gateway:memory:sharedStores:0:readers:0", "*"),
            ("gateway:memory:sharedStores:0:writers:0", "gantry-manager")));

        var registry = provider.GetRequiredService<ISharedMemoryStoreRegistry>();

        registry.CanRead("harbor-relay", "platform-knowledge").ShouldBeTrue();
        registry.CanWrite("gantry-manager", "platform-knowledge").ShouldBeTrue();
        registry.CanWrite("harbor-relay", "platform-knowledge")
            .ShouldBeFalse("readers is a wildcard, writers is not - they are separate lists for a reason");
    }
}

/// <summary>
/// The seam between the operator's config shape and the registry's record.
/// </summary>
public sealed class SharedMemoryStoreConfigMappingTests
{
    [Fact]
    public void Nothing_configured_maps_to_no_stores()
    {
        SharedMemoryStoreConfigMapping.FromConfig(null).ShouldBeEmpty();
        SharedMemoryStoreConfigMapping.FromConfig([]).ShouldBeEmpty();
    }

    [Fact]
    public void An_entry_with_no_name_is_dropped_rather_than_registered()
    {
        // A half-filled form is a real state an operator can save. A nameless store cannot be
        // addressed, granted or written to, so registering it would only defer the failure.
        var mapped = SharedMemoryStoreConfigMapping.FromConfig([
            new SharedMemoryStoreEntry { Name = "   ", Readers = ["*"] },
            new SharedMemoryStoreEntry { Name = null, Readers = ["*"] },
            new SharedMemoryStoreEntry { Name = "real", Readers = ["*"] }
        ]);

        mapped.Count.ShouldBe(1);
        mapped[0].Name.ShouldBe("real");
    }

    [Fact]
    public void A_duplicate_name_does_not_produce_two_stores_with_different_access_lists()
    {
        // The registry resolves names case-insensitively, so a second entry would be a store you
        // can see in config and never reach - with an ACL that silently does not apply.
        var mapped = SharedMemoryStoreConfigMapping.FromConfig([
            new SharedMemoryStoreEntry { Name = "shared", Writers = ["gantry-manager"] },
            new SharedMemoryStoreEntry { Name = "SHARED", Writers = ["*"] }
        ]);

        mapped.Count.ShouldBe(1);
        mapped[0].Writers.ShouldBe(["gantry-manager"]);
    }

    [Fact]
    public void Access_lists_are_trimmed_and_de_duplicated()
    {
        var mapped = SharedMemoryStoreConfigMapping.FromConfig([
            new SharedMemoryStoreEntry
            {
                Name = "  shared  ",
                Readers = [" gantry-manager ", "gantry-manager", "", "   ", "harbor-relay"]
            }
        ]);

        mapped[0].Name.ShouldBe("shared");
        mapped[0].Readers.ShouldBe(["gantry-manager", "harbor-relay"]);
    }

    [Fact]
    public void A_nonsensical_retention_is_treated_as_no_retention_limit()
    {
        // Zero or negative days would otherwise mean "delete everything immediately", which is
        // never what someone who left the box at 0 intended.
        var mapped = SharedMemoryStoreConfigMapping.FromConfig([
            new SharedMemoryStoreEntry { Name = "a", RetentionDays = 0 },
            new SharedMemoryStoreEntry { Name = "b", RetentionDays = -5 },
            new SharedMemoryStoreEntry { Name = "c", RetentionDays = 30 }
        ]);

        mapped[0].RetentionDays.ShouldBeNull();
        mapped[1].RetentionDays.ShouldBeNull();
        mapped[2].RetentionDays.ShouldBe(30);
    }

    [Fact]
    public void Readers_and_writers_stay_separate()
    {
        // The failure mode this guards is a mapping that fills both from one list, quietly making
        // every reader a writer - which turns a curated store into a channel any agent can use to
        // mislead the rest.
        var mapped = SharedMemoryStoreConfigMapping.FromConfig([
            new SharedMemoryStoreEntry { Name = "s", Readers = ["*"], Writers = ["gantry-manager"] }
        ]);

        mapped[0].Readers.ShouldBe(["*"]);
        mapped[0].Writers.ShouldBe(["gantry-manager"]);
    }
}
