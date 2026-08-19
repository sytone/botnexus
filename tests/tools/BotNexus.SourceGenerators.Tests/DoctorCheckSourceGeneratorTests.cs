namespace BotNexus.SourceGenerators.Tests;

using System.Collections.Generic;
using System.Linq;
using Shouldly;

/// <summary>
/// Covers the doctor check/advisory generator (#3319): the three separate registries it emits, the
/// declared ordering it preserves, and the id inventory the docs fence consumes.
/// </summary>
public class DoctorCheckSourceGeneratorTests
{
    /// <summary>
    /// The exact aggregate sequence <c>DoctorCheckRegistry.CreateDefault()</c> carried before the
    /// conversion. #3319 puts reordering operator-visible output explicitly out of scope, so this is
    /// the out-of-scope guard, asserted rather than argued.
    /// </summary>
    private static readonly string[] HandWrittenAggregateOrder =
    [
        "config",
        "world-identity",
        "secret-file-permissions",
        "locations",
        "agent-folders",
        "subagent-workspaces"
    ];

    // ── AC2: the declared order reproduces the hand-written one, from a SHUFFLED input ─────

    [Fact]
    public void AggregateSuite_PreservesTheHandWrittenOrder_EvenWhenDeclarationsArriveShuffled()
    {
        // Roslyn promises no stable enumeration order across the syntax trees of a compilation, so
        // the input is deliberately reversed here. If ordering were derived from arrival order this
        // test would produce the reversed sequence, which is exactly the silent reorder the issue
        // forbids.
        var shuffled = Declarations().Reverse().ToList();

        DoctorCheckCodeGenerator.Select(shuffled, DoctorSuiteNames.Aggregate)
            .Select(check => check.Id)
            .ShouldBe(HandWrittenAggregateOrder);
    }

    [Fact]
    public void ConfigChecksAndAdvisories_AreSeparateLists()
    {
        // DoctorConfigCommand documents the separation as deliberate: an IConfigAdvisory has no
        // Apply and must never be reachable from the --yes loop. The suite is a declared attribute
        // argument, so a heuristic cannot mix them.
        var configIds = DoctorCheckCodeGenerator.Select(Declarations(), DoctorSuiteNames.Config)
            .Select(check => check.Id)
            .ToList();
        var advisoryIds = DoctorCheckCodeGenerator.Select(Declarations(), DoctorSuiteNames.Advisory)
            .Select(check => check.Id)
            .ToList();

        configIds.ShouldContain("feature-flags-explicit");
        advisoryIds.ShouldContain("feature-flags-unknown-key");
        configIds.Intersect(advisoryIds).ShouldBeEmpty();
    }

    // ── AC2: a class carrying the attribute reaches the registry with no other edit ────────

    [Fact]
    public void AddingADeclaration_ReachesBothTheRegistryAndTheIdInventory()
    {
        // This is the #2700 defect made unrepresentable. Before, a check could be written, tested,
        // and left out of the hand-written registry array - compiling cleanly and never running.
        // Here the second list does not exist, so one added declaration necessarily reaches both.
        var extended = Declarations().ToList();
        extended.Add(new DoctorCheckModel
        {
            Id = "brand-new-check",
            Suite = DoctorSuiteNames.Aggregate,
            Order = 99,
            TypeName = "BotNexus.Cli.Commands.Doctor.BrandNewCheck"
        });

        var generated = DoctorCheckCodeGenerator.Generate(extended, "BotNexus.Cli.Commands.Doctor");

        generated.ShouldContain("new global::BotNexus.Cli.Commands.Doctor.BrandNewCheck()");
        generated.ShouldContain("\"brand-new-check\"");
    }

    [Fact]
    public void GeneratedRegistries_EmitOneFactoryPerSuite()
    {
        var generated = DoctorCheckCodeGenerator.Generate(Declarations(), "BotNexus.Cli.Commands.Doctor");

        generated.ShouldContain("IReadOnlyList<IDoctorCheck> CreateAggregate()");
        generated.ShouldContain("IReadOnlyList<IConfigCheck> CreateConfigChecks()");
        generated.ShouldContain("IReadOnlyList<IConfigAdvisory> CreateAdvisories()");
    }

    [Fact]
    public void SuiteName_MapsTheDeclaredEnumValue_AndNeverInfersFromAnythingElse()
    {
        DoctorCheckSourceGenerator.SuiteName(0).ShouldBe(DoctorSuiteNames.Aggregate);
        DoctorCheckSourceGenerator.SuiteName(1).ShouldBe(DoctorSuiteNames.Config);
        DoctorCheckSourceGenerator.SuiteName(2).ShouldBe(DoctorSuiteNames.Advisory);
        DoctorCheckSourceGenerator.SuiteName(null).ShouldBe(
            DoctorSuiteNames.Aggregate,
            "an unreadable suite argument must fall back to the suite with no auto-apply loop");
    }

    [Fact]
    public void TiedOrders_StillProduceADeterministicSequence()
    {
        // Two declarations sharing an Order must not make the emitted registry depend on arrival
        // order - that would reintroduce exactly the nondeterminism the explicit Order removes.
        var tied = new List<DoctorCheckModel>
        {
            new() { Id = "zebra", Suite = DoctorSuiteNames.Config, Order = 3, TypeName = "N.Zebra" },
            new() { Id = "alpha", Suite = DoctorSuiteNames.Config, Order = 3, TypeName = "N.Alpha" }
        };

        DoctorCheckCodeGenerator.Select(tied, DoctorSuiteNames.Config).Select(c => c.Id)
            .ShouldBe(["alpha", "zebra"]);
        DoctorCheckCodeGenerator.Select(Enumerable.Reverse(tied).ToList(), DoctorSuiteNames.Config).Select(c => c.Id)
            .ShouldBe(["alpha", "zebra"]);
    }

    private static IReadOnlyList<DoctorCheckModel> Declarations() =>
    [
        new() { Id = "config", Suite = DoctorSuiteNames.Aggregate, Order = 0, TypeName = "N.ConfigHealthCheck" },
        new() { Id = "world-identity", Suite = DoctorSuiteNames.Aggregate, Order = 1, TypeName = "N.WorldIdCheck" },
        new() { Id = "secret-file-permissions", Suite = DoctorSuiteNames.Aggregate, Order = 2, TypeName = "N.SecretFilePermissionCheck" },
        new() { Id = "locations", Suite = DoctorSuiteNames.Aggregate, Order = 3, TypeName = "N.LocationAccessibilityCheck" },
        new() { Id = "agent-folders", Suite = DoctorSuiteNames.Aggregate, Order = 4, TypeName = "N.PersistentAgentFolderCheck" },
        new() { Id = "subagent-workspaces", Suite = DoctorSuiteNames.Aggregate, Order = 5, TypeName = "N.SubAgentWorkspaceCheck" },
        new() { Id = "extensions-block", Suite = DoctorSuiteNames.Config, Order = 0, TypeName = "N.ExtensionsBlockCheck" },
        new() { Id = "feature-flags-explicit", Suite = DoctorSuiteNames.Config, Order = 7, TypeName = "N.FeatureFlagSeedCheck" },
        new() { Id = "gateway-wildcard-bind", Suite = DoctorSuiteNames.Advisory, Order = 0, TypeName = "N.WildcardListenUrlAdvisory" },
        new() { Id = "feature-flags-unknown-key", Suite = DoctorSuiteNames.Advisory, Order = 1, TypeName = "N.UnknownFeatureFlagAdvisory" }
    ];
}
