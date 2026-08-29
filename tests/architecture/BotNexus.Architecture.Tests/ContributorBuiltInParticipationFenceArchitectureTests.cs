using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Build-failing fence for issue #3539, acceptance criterion 5: every contributor interface the
/// gateway defines must either be implemented by a built-in capability, or appear in the exemption
/// map below with a written reason.
/// </summary>
/// <remarks>
/// <para>
/// The standing platform direction is that built-in capabilities register through the same
/// contracts as extensions, so the extension path cannot rot. A contract that only extensions
/// implement is exercised by nothing the core depends on, and decays silently until the next
/// extension author discovers it never worked.
/// </para>
/// <para>
/// #3539 was that scan, run by hand. Running it by hand does not scale and does not survive: the
/// audit's own table was already stale against the tree it described. This fence converts the scan
/// into a build assertion, so a NEWLY added contributor interface fails the build until someone
/// either gives it a built-in implementation or records why it does not have one. The exemption
/// map is deliberately a source-level literal rather than an attribute: the reason is the artefact
/// that matters, and a future scan reads it here instead of re-deriving whether an asymmetry was
/// intentional.
/// </para>
/// <para>
/// The scan strips comments before matching, or the fence fires on the prose in this very file
/// that legitimately names the banned shapes (the #2813 / #2955 lesson).
/// </para>
/// </remarks>
public sealed class ContributorBuiltInParticipationFenceArchitectureTests : ArchitectureTest
{
    /// <summary>
    /// Contributor interfaces with no built-in implementation, each mapped to the recorded reason.
    /// Adding an entry here is a deliberate, reviewed decision; adding a contributor interface
    /// without either a built-in or an entry here is a build failure.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ExemptContributors =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["IAgentToolContributor"] =
                "Deliberate. The gateway has three tool paths and composes all three per handle in " +
                "InProcessIsolationStrategy: IAgentToolFactory (per-agent built-in, already carries " +
                "workspace, path validator and shell command), IToolRegistry (flat, for " +
                "agent-invariant built-ins), and this contract (extensions). What this contract " +
                "adds over IAgentToolFactory is asynchrony, handle-scoped ResourcesToDispose, and " +
                "construction with no compile-time Gateway reference - the last of which is worth " +
                "nothing to a built-in, and the first two of which no built-in needs today. A " +
                "built-in that later needs async construction or handle-scoped disposal SHOULD " +
                "move here; that migration needs no new decision. See the remarks on the " +
                "interface and on IToolRegistry.",

            ["IServiceContributor"] =
                "By design. This contract exists so a dynamically loaded extension assembly can " +
                "register services into the host container. Built-in gateway services are " +
                "registered directly by the composition root, which is the same mechanism reached " +
                "by a different route - not a privileged bypass. A built-in implementation would " +
                "be ceremony around a call the composition root already makes.",

            ["IApiContributor"] =
                "Zero implementations anywhere (#3539). Resolved and invoked by " +
                "AssemblyLoadContextExtensionLoader.MapExtensionEndpoints, but nothing implements " +
                "it, so the route-scoping behaviour has never executed - including the unresolved " +
                "TODO about deriving the extension id. Retained pending the keep-or-remove " +
                "decision; an unimplemented contract cannot be known to work.",

            ["IPromptContributor"] =
                "Zero implementations anywhere (#3539), AND not collected on any production path: " +
                "SystemPromptBuilder composes its PromptPipeline exclusively via Add(IPromptSection) " +
                "and never calls AddContributors, so an implementation would be silently ignored. " +
                "Retained pending the keep-or-remove decision.",
        };

    /// <summary>
    /// Contributor interfaces that DO have built-in participation today. Pinning the expected set
    /// stops a regression where a built-in implementation is deleted and the interface quietly
    /// slides into extension-only territory without anyone recording a reason.
    /// </summary>
    private static readonly IReadOnlyList<string> ExpectedBuiltInParticipants =
    [
        "ICommandContributor",
        "IConfigSchemaContributor",
        "IEndpointContributor",
    ];

    /// <summary>
    /// Every contributor interface is either implemented by a built-in or explicitly exempted with
    /// a reason. This is the assertion that makes the #3539 hand-scan unnecessary.
    /// </summary>
    [Fact]
    public void EveryContributorInterface_HasBuiltInParticipationOrARecordedExemption()
    {
        var undocumented = ContributorInterfaces()
            .Where(name => !HasBuiltInImplementation(name) && !ExemptContributors.ContainsKey(name))
            .Order()
            .ToList();

        undocumented.ShouldBeEmpty(
            "Every contributor interface must be implemented by a built-in capability, or carry a " +
            "recorded reason in ExemptContributors. Built-in capabilities are expected to use the " +
            "same contracts as extensions so the extension path stays exercised; a contract only " +
            "extensions implement rots silently, which is the specific failure #3539 exists to " +
            "end. If the asymmetry is deliberate, say so here and at the seam in source - an " +
            "asymmetry with no recorded reason is indistinguishable from drift on the next scan." +
            "\nUndocumented: " + string.Join(", ", undocumented));
    }

    /// <summary>
    /// The exemption map may not accumulate stale entries. Once a contract gains a built-in
    /// implementation, its "no built-in" reason is false and must be removed rather than left to
    /// mislead the next reader.
    /// </summary>
    [Fact]
    public void ExemptionMap_ContainsNoContractThatNowHasABuiltIn()
    {
        var stale = ExemptContributors.Keys
            .Where(HasBuiltInImplementation)
            .Order()
            .ToList();

        stale.ShouldBeEmpty(
            "These contracts are listed as having no built-in implementation, but one now exists. " +
            "Remove the exemption - a recorded reason that is no longer true is worse than none." +
            "\nStale: " + string.Join(", ", stale));
    }

    /// <summary>
    /// The exemption map may not silently reference a contract that no longer exists, and every
    /// reason must actually be a reason rather than a placeholder.
    /// </summary>
    [Fact]
    public void ExemptionMap_ReferencesOnlyLiveContractsAndCarriesRealReasons()
    {
        var interfaces = ContributorInterfaces();

        var phantom = ExemptContributors.Keys
            .Where(name => !interfaces.Contains(name))
            .Order()
            .ToList();

        phantom.ShouldBeEmpty(
            "The exemption map names contributor interfaces that no longer exist in src/. Delete " +
            "the entries with the contracts.\nPhantom: " + string.Join(", ", phantom));

        foreach (var (name, reason) in ExemptContributors)
        {
            reason.Length.ShouldBeGreaterThan(
                80,
                $"The exemption reason for {name} must explain WHY the asymmetry is acceptable. " +
                "A one-line placeholder defeats the purpose of recording it.");
        }
    }

    /// <summary>
    /// The contracts that have built-in participation today keep it. Deleting the only built-in
    /// implementation of a contract is exactly the rot #3539 describes, and it must not be
    /// achievable without this fence going red.
    /// </summary>
    [Fact]
    public void ContractsWithBuiltInParticipation_KeepIt()
    {
        var regressed = ExpectedBuiltInParticipants
            .Where(name => !HasBuiltInImplementation(name))
            .Order()
            .ToList();

        regressed.ShouldBeEmpty(
            "These contracts had a built-in implementation and no longer do. Restore it, or move " +
            "the contract into ExemptContributors with a written reason.\nRegressed: "
            + string.Join(", ", regressed));
    }

    /// <summary>
    /// AC3: the rationale must live at the seam in source, not only in this test. A future reader
    /// arrives via the interface file, not via the fence.
    /// </summary>
    [Fact]
    public void SeamFiles_CarryTheRecordedRationale()
    {
        var seams = new (string File, string Marker)[]
        {
            ("src/gateway/BotNexus.Gateway.Abstractions/Agents/IAgentToolContributor.cs", "#3539"),
            ("src/gateway/BotNexus.Gateway/Agents/IToolRegistry.cs", "#3539"),
            ("src/gateway/BotNexus.Gateway.Abstractions/Extensions/IApiContributor.cs", "#3539"),
            ("src/gateway/BotNexus.Gateway.Prompts/IPromptContributor.cs", "#3539"),
        };

        foreach (var (file, marker) in seams)
        {
            var path = Path.Combine(Repository.Root, file);
            File.Exists(path).ShouldBeTrue($"Expected seam file at {file}.");
            File.ReadAllText(path).ShouldContain(
                marker,
                Case.Sensitive,
                $"{file} must state the #3539 decision at the seam. An asymmetry with no recorded " +
                "reason is indistinguishable from drift on the next scan.");
        }
    }

    /// <summary>
    /// Anti-vacuity. A fence whose discovery resolves to an empty set, or whose detector matches
    /// nothing, silently guards nothing.
    /// </summary>
    [Fact]
    public void ContributorFenceDetectors_AreNotVacuous()
    {
        var interfaces = ContributorInterfaces();

        interfaces.Count.ShouldBeGreaterThanOrEqualTo(
            7,
            "The scan should find every contributor interface in src/; a near-empty set means " +
            "path resolution broke and the fence stopped guarding anything. Found: "
            + string.Join(", ", interfaces.Order()));

        interfaces.ShouldContain("IAgentToolContributor");
        interfaces.ShouldContain("IPromptContributor");

        // Detector must match the plain declaration shape.
        ImplementsProbe("IEndpointContributor")
            .IsMatch("public sealed class TelemetryEndpointContributor : IEndpointContributor")
            .ShouldBeTrue("Detector must match a simple base-list implementation.");

        // Detector must match the primary-constructor shape, where the base list is many lines
        // below the class keyword. BuiltInCommandContributor is exactly this shape, and a detector
        // that misses it would report the flagship built-in as absent.
        ImplementsProbe("ICommandContributor")
            .IsMatch(
                "internal sealed class BuiltInCommandContributor(\n" +
                "    IAgentRegistry agentRegistry,\n" +
                "    ISessionCompactionCoordinator? compactionCoordinator = null) : ICommandContributor")
            .ShouldBeTrue("Detector must match a primary-constructor base list spanning lines.");

        // Detector must not fire on the interface's own declaration, nor on a mere mention.
        ImplementsProbe("IApiContributor")
            .IsMatch("public interface IApiContributor")
            .ShouldBeFalse("Detector must not treat the declaration as an implementation.");
        ImplementsProbe("IApiContributor")
            .IsMatch("foreach (var c in app.Services.GetServices<IApiContributor>())")
            .ShouldBeFalse("Detector must not treat a resolution site as an implementation.");
        ImplementsProbe("IPromptContributor")
            .IsMatch("private readonly List<IPromptContributor> _contributors = [];")
            .ShouldBeFalse("Detector must not treat a field declaration as an implementation.");

        // The two zero-implementation contracts must genuinely have none, or the exemption
        // reasons recorded above are fiction.
        HasBuiltInImplementation("IApiContributor").ShouldBeFalse();
        HasBuiltInImplementation("IPromptContributor").ShouldBeFalse();

        // And the flagship built-in must genuinely be found by the real filesystem scan, not just
        // by the string probes above.
        HasBuiltInImplementation("ICommandContributor").ShouldBeTrue(
            "BuiltInCommandContributor is the model this fence is built around; failing to find " +
            "it means the scan is not reading src/gateway.");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every contributor interface declared under <c>src/</c>, by simple name.
    /// </summary>
    private IReadOnlySet<string> ContributorInterfaces() =>
        SourceFiles(Repository.SourceRoot)
            .SelectMany(f => InterfaceDeclarationProbe().Matches(StripComments(File.ReadAllText(f))))
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// True when a type outside <c>src/extensions/</c> implements <paramref name="contract"/>.
    /// "Built-in" means shipped in the gateway itself rather than in a loadable extension
    /// assembly - the distinction the audit turns on.
    /// </summary>
    private bool HasBuiltInImplementation(string contract)
    {
        var probe = ImplementsProbe(contract);
        return SourceFiles(Repository.SourceRoot)
            .Where(IsBuiltIn)
            .Any(f => probe.IsMatch(StripComments(File.ReadAllText(f))));
    }

    private static bool IsBuiltIn(string file) =>
        !file.Contains(
            $"{Path.DirectorySeparatorChar}extensions{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal);

    private static IReadOnlyList<string> SourceFiles(string root) =>
        Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

    private static readonly Regex s_interfaceDeclarationProbe = new(
        @"\binterface\s+(?<name>I\w*Contributor)\b", RegexOptions.Compiled);

    private static Regex InterfaceDeclarationProbe() => s_interfaceDeclarationProbe;

    /// <summary>
    /// Matches a class/record/struct declaration whose base list names <paramref name="contract"/>.
    /// The region between the type keyword and the base list is bounded by <c>{</c> and <c>;</c>,
    /// which keeps a primary constructor's parameter list in scope while stopping the match from
    /// running past the declaration into the body.
    /// </summary>
    private static Regex ImplementsProbe(string contract) =>
        new(@"\b(?:class|record|struct)\s+\w+[^{};]*?:\s*[^{};]*?\b"
            + Regex.Escape(contract) + @"\b",
            RegexOptions.Compiled | RegexOptions.Singleline);

    private static string StripComments(string source)
    {
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"//[^\r\n]*", string.Empty);
    }
}
