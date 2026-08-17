using System.Text.Json;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Extensions.Channels.Test.Tests;

/// <summary>
/// The opt-in guarantee (#326 AC7): the test channel must never be loaded unless a configuration
/// deliberately turns it on.
/// </summary>
/// <remarks>
/// <para>
/// This matters more than for any other extension in the tree. The test channel exposes an
/// UNAUTHENTICATED HTTP endpoint that injects arbitrary messages into the gateway as if a real user
/// had sent them. Shipping that enabled would be a remote message-injection surface, so "it is only
/// for tests" has to be enforced by the build rather than by convention.
/// </para>
/// <para>
/// Two independent conditions are tested, because each on its own would still leave the extension
/// loadable: the shipped manifest must declare <c>enabled: false</c>, and the loader must actually
/// honour that flag when discovering the real shipped manifest.
/// </para>
/// </remarks>
public sealed class TestChannelOptInTests : IDisposable
{
    private readonly string _root;

    public TestChannelOptInTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "botnexus-test-channel-optin", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// The SHIPPED manifest — the one copied to the output directory and deployed — must be
    /// disabled. Reading the real file rather than a fixture is deliberate: a fixture would assert
    /// a fact about the test, not about what actually ships.
    /// </summary>
    [Fact]
    public void ShippedManifest_DeclaresTheExtensionDisabled()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "botnexus-extension.json");
        File.Exists(manifestPath).ShouldBeTrue(
            $"The test-channel manifest was not copied to the output directory ({manifestPath}). "
            + "Without it the extension cannot be discovered at all, and this opt-in assertion is vacuous.");

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        document.RootElement.GetProperty("id").GetString().ShouldBe("botnexus-test-channel");
        document.RootElement.GetProperty("enabled").GetBoolean().ShouldBeFalse(
            "The test channel exposes an unauthenticated inbound-injection endpoint. Its manifest "
            + "must ship disabled so no production configuration loads it. See issue #326 AC7.");
    }

    /// <summary>
    /// End-to-end through the real loader: a production-shaped configuration that simply points at
    /// the extensions directory must load NOTHING from the test channel, and must therefore
    /// register neither the adapter nor its HTTP surface.
    /// </summary>
    [Fact]
    public async Task ProductionConfiguration_DoesNotLoadTheTestChannel()
    {
        StageShippedExtension();
        var services = new ServiceCollection().AddLogging();

        var results = await services.LoadConfiguredExtensionsAsync(
            ProductionConfig(),
            NullLoggerFactory.Instance);

        results.ShouldBeEmpty(
            "A production configuration loaded the test channel. The manifest's enabled flag is the "
            + "only thing standing between a normal deployment and an unauthenticated message-injection "
            + "endpoint.");

        HasRegistration(services, typeof(IChannelAdapter), typeof(TestChannelAdapter)).ShouldBeFalse(
            "The test channel adapter was registered by a production configuration.");
        HasRegistration(services, typeof(IEndpointContributor), typeof(TestChannelEndpointContributor)).ShouldBeFalse(
            "The test channel HTTP surface was registered by a production configuration.");
    }

    /// <summary>
    /// Non-vacuity guard for the test above. Enabling the SAME staged manifest must load the
    /// extension and register both contracts — otherwise the negative assertion could be passing
    /// because the directory was empty, the manifest unreadable, or the assembly unresolvable, and
    /// would keep passing even if the enabled flag were flipped.
    /// </summary>
    [Fact]
    public async Task OptingInLoadsTheTestChannelAndRegistersItsSurface()
    {
        var directory = StageShippedExtension();
        EnableStagedManifest(directory);
        var services = new ServiceCollection().AddLogging();

        var results = await services.LoadConfiguredExtensionsAsync(
            ProductionConfig(),
            NullLoggerFactory.Instance);

        var result = results.ShouldHaveSingleItem();
        result.ExtensionId.ShouldBe("botnexus-test-channel");
        result.Success.ShouldBeTrue(result.Error);

        // Registered by CONTRACT, matched by type NAME: the loader resolves the extension in its own
        // AssemblyLoadContext, so the loaded TestChannelAdapter is not reference-equal to the one
        // this test project compiled against.
        HasRegistration(services, typeof(IChannelAdapter), typeof(TestChannelAdapter)).ShouldBeTrue(
            "The opted-in test channel did not register an IChannelAdapter.");
        HasRegistration(services, typeof(IEndpointContributor), typeof(TestChannelEndpointContributor)).ShouldBeTrue(
            "The opted-in test channel did not register its IEndpointContributor.");
    }

    /// <summary>
    /// Matches a registration by contract and by implementation type FULL NAME. Reference equality
    /// cannot be used: the loader activates the extension in its own <c>AssemblyLoadContext</c>, so
    /// its <c>TestChannelAdapter</c> is a distinct <c>Type</c> from the one compiled here.
    /// </summary>
    private static bool HasRegistration(IServiceCollection services, Type contract, Type implementation)
        => services.Any(descriptor =>
            descriptor.ServiceType == contract
            && descriptor.ImplementationType is not null
            && descriptor.ImplementationType.FullName == implementation.FullName);

    /// <summary>
    /// Copies the extension's real build output (manifest + assemblies) into a temporary probe
    /// directory laid out the way the deploy step and the container image lay it out.
    /// </summary>
    private string StageShippedExtension()
    {
        var destination = Path.Combine(_root, "extensions", "BotNexus.Extensions.Channels.Test");
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "botnexus-extension.json"),
            Path.Combine(destination, "botnexus-extension.json"),
            overwrite: true);

        return destination;
    }

    private static void EnableStagedManifest(string directory)
    {
        var path = Path.Combine(directory, "botnexus-extension.json");
        var json = File.ReadAllText(path);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["enabled"] = true;
        File.WriteAllText(path, node.ToJsonString());
    }

    private PlatformConfig ProductionConfig() => new()
    {
        Gateway = new()
        {
            Extensions = new ExtensionsConfig
            {
                Enabled = true,
                Path = Path.Combine(_root, "extensions"),
            },
        },
    };
}
