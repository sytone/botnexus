using System.IO.Abstractions;
using System.Text.Json;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.FeatureManagement;
using Shouldly;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Behaviour tests for the per-home world identity token (#2834): the GUID written to
/// <c>config.json</c> as <c>worldId</c>, resolved exactly once and handed out as a single injected
/// <see cref="WorldId"/> dependency.
/// </summary>
/// <remarks>
/// These cover acceptance criteria 1-4 of #2834. Criterion 5 (single derivation) is enforced
/// categorically by <c>WorldIdSingleDerivationArchitectureTests</c> rather than by a behaviour test,
/// because "nobody else reads the key" is a property of the whole source tree, not of one code path.
/// </remarks>
public sealed class WorldIdTests
{
    /// <summary>Acceptance criterion 1: <c>worldId</c> is a recognised property and reaches the schema.</summary>
    [Fact]
    public void WorldId_IsRecognisedConfigProperty_AndAppearsInGeneratedSchema()
    {
        var config = JsonSerializer.Deserialize<PlatformConfig>(
            """{ "worldId": "6f0a2f0e-6a2e-4a2f-9c1a-8b0d3e5f7a91" }""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        config.ShouldNotBeNull();
        config!.WorldId.ShouldBe("6f0a2f0e-6a2e-4a2f-9c1a-8b0d3e5f7a91");

        PlatformConfigSchema.GenerateSchemaJson().ShouldContain("worldId");
    }

    /// <summary>
    /// Acceptance criterion 2: a home whose <c>config.json</c> carries no <c>worldId</c> gets one
    /// generated, persisted, and present in the file after start.
    /// </summary>
    [Fact]
    public async Task Start_AgainstHomeWithoutWorldId_GeneratesAndPersistsIt()
    {
        using var home = new WorldHomeFixture();
        home.WriteConfig("""{ "version": 1 }""");

        var (worldId, _) = await StartWorldAsync(home.ConfigPath);

        var persisted = ReadWorldIdFromFile(home.ConfigPath);
        persisted.ShouldNotBeNull();
        persisted.ShouldBe(worldId.Value);
        Guid.Parse(persisted!).ShouldNotBe(Guid.Empty);
    }

    /// <summary>
    /// Acceptance criterion 3: a home that already has a <c>worldId</c> is not modified - asserted by
    /// comparing the file content byte-for-byte before and after start, so a reformat or a rewrite
    /// with the same value would also fail.
    /// </summary>
    [Fact]
    public async Task Start_AgainstHomeWithExistingWorldId_LeavesFileUntouched()
    {
        const string existing = "11111111-2222-3333-4444-555555555555";
        using var home = new WorldHomeFixture();
        home.WriteConfig($$"""{ "version": 1, "worldId": "{{existing}}" }""");

        var before = File.ReadAllText(home.ConfigPath);

        var (worldId, _) = await StartWorldAsync(home.ConfigPath);

        worldId.Value.ShouldBe(existing);
        File.ReadAllText(home.ConfigPath).ShouldBe(before);
    }

    /// <summary>
    /// Acceptance criterion 4: two gateways started against two different homes in the SAME test run
    /// resolve two different world IDs. This is the clause that proves the value is per-home and not
    /// per-machine or static - a resolver hard-coded to any constant, or one falling back to
    /// <c>Environment.MachineName</c>, fails here by name (criterion 7).
    /// </summary>
    [Fact]
    public async Task TwoHomesInSameRun_ResolveDifferentWorldIds()
    {
        using var first = new WorldHomeFixture();
        using var second = new WorldHomeFixture();
        first.WriteConfig("""{ "version": 1 }""");
        second.WriteConfig("""{ "version": 1 }""");

        var (firstId, _) = await StartWorldAsync(first.ConfigPath);
        var (secondId, _) = await StartWorldAsync(second.ConfigPath);

        firstId.Value.ShouldNotBe(secondId.Value);
        ReadWorldIdFromFile(first.ConfigPath).ShouldBe(firstId.Value);
        ReadWorldIdFromFile(second.ConfigPath).ShouldBe(secondId.Value);
        ReadWorldIdFromFile(first.ConfigPath).ShouldNotBe(ReadWorldIdFromFile(second.ConfigPath));
    }

    /// <summary>
    /// Acceptance criterion 5 (injection half): the resolved value is reachable as a single injected
    /// dependency, and the instance the persistence path used is the same instance consumers get.
    /// </summary>
    [Fact]
    public async Task ResolvedWorldId_IsASingleInjectedSingleton()
    {
        using var home = new WorldHomeFixture();
        home.WriteConfig("""{ "version": 1 }""");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFeatureManager>(new AllOffFeatureManager());
        services.AddSingleton<IFileSystem>(new FileSystem());
        services.AddPlatformConfiguration(home.ConfigPath);
        await using var provider = services.BuildServiceProvider();

        await RunWorldIdBootstrapAsync(services, provider);

        var first = provider.GetRequiredService<WorldId>();
        var second = provider.GetRequiredService<WorldId>();

        first.ShouldBeSameAs(second);
        first.Value.ShouldBe(ReadWorldIdFromFile(home.ConfigPath));
    }

    /// <summary>
    /// Builds the gateway configuration registrations against <paramref name="configPath"/> and runs
    /// the world-identity bootstrap, i.e. everything a real start does for this feature.
    /// </summary>
    private static async Task<(WorldId Id, WorldIdOrigin Origin)> StartWorldAsync(string configPath)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFeatureManager>(new AllOffFeatureManager());
        services.AddSingleton<IFileSystem>(new FileSystem());
        services.AddPlatformConfiguration(configPath);
        await using var provider = services.BuildServiceProvider();

        await RunWorldIdBootstrapAsync(services, provider);

        return (provider.GetRequiredService<WorldId>(), provider.GetRequiredService<WorldIdOrigin>());
    }

    /// <summary>
    /// Runs the world-identity bootstrap exactly as a real host start would.
    /// </summary>
    /// <remarks>
    /// The registration is asserted through the descriptor list rather than by enumerating
    /// <see cref="IHostedService"/> from the provider: enumerating would activate every other hosted
    /// service the gateway registers (agent reconciliation, config hydration, the shadow migration),
    /// dragging in the entire runtime graph to test one bootstrap. Checking the descriptor keeps the
    /// registration itself under test - remove <c>AddHostedService&lt;WorldIdPersistenceService&gt;()</c>
    /// and this fails - while activating only the service in question.
    /// </remarks>
    private static async Task RunWorldIdBootstrapAsync(IServiceCollection services, IServiceProvider provider)
    {
        services.ShouldContain(
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(WorldIdPersistenceService),
            "WorldIdPersistenceService must be registered as a hosted service so the identity is persisted on start.");

        var bootstrap = ActivatorUtilities.CreateInstance<WorldIdPersistenceService>(provider);
        await bootstrap.StartAsync(CancellationToken.None);
    }

    /// <summary>
    /// Every flag off. <c>AddPlatformConfiguration</c> registers the config-shadow gate, which takes an
    /// <see cref="IFeatureManager"/> the host normally supplies; these tests are not exercising that
    /// path and an all-off manager keeps the shadow migration inert, which is also its production
    /// default.
    /// </summary>
    private sealed class AllOffFeatureManager : IFeatureManager
    {
        public async IAsyncEnumerable<string> GetFeatureNamesAsync()
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> IsEnabledAsync(string feature) => Task.FromResult(false);

        public Task<bool> IsEnabledAsync<TContext>(string feature, TContext context) => Task.FromResult(false);
    }

    private static string? ReadWorldIdFromFile(string configPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        return document.RootElement.TryGetProperty("worldId", out var element)
            && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
    }

    private sealed class WorldHomeFixture : IDisposable
    {
        public WorldHomeFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "botnexus-world-id-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            ConfigPath = Path.Combine(RootPath, "config.json");
        }

        public string RootPath { get; }

        public string ConfigPath { get; }

        public void WriteConfig(string json) => File.WriteAllText(ConfigPath, json);

        public void Dispose()
        {
            try { Directory.Delete(RootPath, recursive: true); }
            catch (IOException) { /* best effort temp cleanup */ }
        }
    }
}

/// <summary>Direct unit coverage for the one place a world ID is derived.</summary>
public sealed class WorldIdResolverTests
{
    [Fact]
    public void TryRead_WithMissingFile_ReturnsNull()
        => WorldIdResolver
            .TryRead(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.json"), new FileSystem())
            .ShouldBeNull();

    [Fact]
    public void Resolve_TwiceOnAnEmptyHome_MintsDistinctIds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "botnexus-world-id-resolver", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, "{}");

            var first = WorldIdResolver.Resolve(path, new FileSystem(), out var firstGenerated);
            var second = WorldIdResolver.Resolve(path, new FileSystem(), out var secondGenerated);

            firstGenerated.ShouldBeTrue();
            secondGenerated.ShouldBeTrue();
            first.Id.ShouldNotBe(second.Id);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
