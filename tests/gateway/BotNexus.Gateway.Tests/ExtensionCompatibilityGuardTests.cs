using System.Reflection;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Pins the gateway-contract compatibility guard.
/// </summary>
/// <remarks>
/// A prebuilt extension delivered by a marketplace plugin was compiled somewhere else, against some
/// other gateway. Without this check a mismatch surfaces as a type or method load error deep inside
/// startup, or - worse - loads and binds against a subtly different contract. The load-bearing
/// tests are therefore the two refusals and, just as importantly, the three cases that must NOT
/// refuse: no declaration, an empty declaration, and a version string this parser does not
/// understand. The field is new and almost every extension omits it, so a guard that failed open
/// incorrectly would break working deployments to enforce a rule nobody opted into.
/// </remarks>
public sealed class ExtensionCompatibilityGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "botnexus-abi-guard", Guid.NewGuid().ToString("N"));

    public ExtensionCompatibilityGuardTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>The contract version the guard compares against, as the loader resolves it.</summary>
    private static Version Current => AssemblyLoadContextExtensionLoader.AbstractionsVersion;

    private static AssemblyLoadContextExtensionLoader Loader() =>
        new(new ServiceCollection(),
            new HookDispatcher(),
            NullLogger<AssemblyLoadContextExtensionLoader>.Instance,
            new FileSystem());

    /// <summary>
    /// Builds an extension whose entry assembly is a real file, so a failure can only come from the
    /// guard rather than from a missing path.
    /// </summary>
    private ExtensionInfo Extension(ExtensionCompatibility? compatibility)
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        // A real, loadable assembly: this test assembly itself. The guard runs before the load, so
        // what matters is that the path exists and the manifest is well formed.
        var entry = Assembly.GetExecutingAssembly().Location;

        return new ExtensionInfo
        {
            DirectoryPath = dir,
            ManifestPath = Path.Combine(dir, "botnexus-extension.json"),
            EntryAssemblyPath = entry,
            Manifest = new ExtensionManifest
            {
                Id = "abi-" + Guid.NewGuid().ToString("N")[..8],
                Name = "ABI test",
                Version = "1.0.0",
                EntryAssembly = Path.GetFileName(entry),
                Compatibility = compatibility,
            },
        };
    }

    private async Task<ExtensionLoadResult> LoadAsync(ExtensionCompatibility? compatibility) =>
        await Loader().LoadAsync(Extension(compatibility));

    // Built for a newer gateway than this one.
    [Fact]
    public async Task Refuses_an_extension_whose_floor_is_above_this_gateway()
    {
        var tooNew = new Version(Current.Major + 1, 0, 0).ToString();

        var result = await LoadAsync(new ExtensionCompatibility { MinAbstractionsVersion = tooNew });

        Assert.False(result.Success);
        Assert.Contains("or newer", result.Error);
        Assert.Contains(Current.ToString(), result.Error);
        Assert.Contains(tooNew, result.Error);
    }

    // The ceiling is EXCLUSIVE, so a ceiling equal to the current version must refuse.
    [Fact]
    public async Task Refuses_an_extension_whose_ceiling_this_gateway_has_reached()
    {
        var result = await LoadAsync(new ExtensionCompatibility
        {
            MaxAbstractionsVersion = Current.ToString(),
        });

        Assert.False(result.Success);
        Assert.Contains("needs updating", result.Error);
    }

    /// <summary>
    /// Runs the guard alone. The accept cases assert on the GUARD rather than on a completed load,
    /// because a full load exercises assembly scanning and service registration that can fail for
    /// reasons having nothing to do with compatibility - which would make a green test here mean
    /// nothing. The two refusals above go through the real load path and pin the wiring.
    /// </summary>
    private static Exception? GuardResult(ExtensionCompatibility? compatibility) =>
        Record.Exception(() => AssemblyLoadContextExtensionLoader.ValidateCompatibility(
            new ExtensionManifest { Id = "x", Compatibility = compatibility }));

    [Fact]
    public void Accepts_a_version_inside_the_declared_range()
    {
        Assert.Null(GuardResult(new ExtensionCompatibility
        {
            MinAbstractionsVersion = "0.0.1",
            MaxAbstractionsVersion = new Version(Current.Major + 1, 0, 0).ToString(),
        }));
    }

    // The overwhelmingly common case: every extension written before this field existed.
    [Fact]
    public void No_compatibility_declaration_constrains_nothing()
    {
        Assert.Null(GuardResult(null));
    }

    [Fact]
    public void An_empty_compatibility_block_constrains_nothing()
    {
        Assert.Null(GuardResult(new ExtensionCompatibility()));
    }

    // Failing open on a bound this parser cannot read is deliberate: rejecting an extension because
    // its author wrote an unexpected version string would break a working deployment to enforce a
    // rule they never opted into.
    [Theory]
    [InlineData("not-a-version")]
    [InlineData("^1.4.0")]
    [InlineData(">=2.0")]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unparseable_bound_is_treated_as_no_constraint(string bound)
    {
        Assert.Null(GuardResult(new ExtensionCompatibility { MinAbstractionsVersion = bound }));
    }

    // Shapes an author actually writes must all work.
    [Theory]
    [InlineData("0")]
    [InlineData("0.1")]
    [InlineData("0.0.1")]
    [InlineData("0.0.0.1")]
    public void Common_version_shapes_are_understood(string floor)
    {
        Assert.Null(GuardResult(new ExtensionCompatibility { MinAbstractionsVersion = floor }));
    }

    // Non-vacuity: the same helper must be able to FAIL, or every assertion above is trivially null.
    [Fact]
    public void The_guard_helper_does_reject_when_it_should()
    {
        var tooNew = new Version(Current.Major + 1, 0, 0).ToString();

        Assert.NotNull(GuardResult(new ExtensionCompatibility { MinAbstractionsVersion = tooNew }));
    }

    // The version compared against must be the real one, not a placeholder.
    [Fact]
    public void The_contract_version_is_resolved_from_a_loaded_assembly()
    {
        Assert.NotEqual(new Version(0, 0, 0, 0), Current);
    }
}
