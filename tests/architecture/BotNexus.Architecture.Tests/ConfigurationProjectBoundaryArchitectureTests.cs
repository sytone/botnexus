using System.Reflection;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function pinning the configuration project's dependency direction (#2765).
///
/// <para>
/// <b>Why this exists.</b> Configuration was a <em>folder</em> inside <c>BotNexus.Gateway</c>, not a
/// project. A folder has no boundary: every type in it was reachable by any of the ten projects
/// referencing the gateway assembly, and consumers were free to invent their own access pattern. Two
/// of them did - #2764 records two <c>doctor config</c> checks reading <c>root["compaction"]</c> while
/// the setting lives at <c>gateway.compaction</c>. A wrong traversal returns null, which is
/// indistinguishable from "not configured", so the check reported a correctly-configured platform as
/// broken on every run and its sibling guard was silently inert. Same shape as #2700: a rule
/// structurally incapable of firing reads as a clean pass.
/// </para>
///
/// <para>
/// <b>What the boundary buys.</b> Once configuration is a leaf project, changing the storage medium is
/// one project's internals rather than a cross-cutting change across 228 consuming files. That is the
/// seam #2646 (SQLite-backed config store) needs, and #2766 (shadow-mode validation) is where both
/// implementations and the diff would live. Without the boundary, the store would be implemented
/// against ambient access and every consumer would be a potential caller.
/// </para>
///
/// <para>
/// <b>The direction is the invariant, and it is asserted on the assembly graph rather than on source
/// text.</b> A source-level check (grep for <c>using BotNexus.Gateway.</c>) would have <em>passed</em>
/// on the pre-extraction tree while the coupling was real: same-assembly name resolution let
/// <c>PlatformConfigValidator</c> reference <c>Agents.BuiltInArchetypes</c> with no <c>using</c> at
/// all, and the one <c>using BotNexus.Gateway.Agents</c> that did exist was dead. Only the compiler
/// found the true coupling. So this fence reads <see cref="Assembly.GetReferencedAssemblies"/>, which
/// cannot be fooled by an unused directive or by implicit resolution.
/// </para>
/// </summary>
public sealed class ConfigurationProjectBoundaryArchitectureTests
{
    private const string ConfigurationAssembly = "BotNexus.Gateway.Configuration";
    private const string GatewayAssembly = "BotNexus.Gateway";

    private static Assembly ConfigAssembly => typeof(PlatformConfig).Assembly;

    /// <summary>
    /// The configuration types live in their own assembly, not inside the gateway (AC1).
    /// </summary>
    [Fact]
    public void ConfigurationTypes_LiveInTheirOwnAssembly()
    {
        ConfigAssembly.GetName().Name.ShouldBe(
            ConfigurationAssembly,
            $"Configuration types must live in {ConfigurationAssembly}. Finding them in another " +
            "assembly means the extraction was partially reverted, and the boundary that makes the " +
            "storage medium swappable (#2646) no longer exists.");
    }

    /// <summary>
    /// The configuration project must not depend on the gateway (AC2).
    ///
    /// <para>
    /// This is the clause that makes the direction enforceable rather than conventional. A dependency
    /// from configuration back to <c>BotNexus.Gateway</c> would re-create the cycle the extraction
    /// removed and make configuration un-referenceable from anything below the gateway.
    /// </para>
    /// </summary>
    [Fact]
    public void ConfigurationProject_DoesNotReferenceTheGateway()
    {
        var offenders = ConfigAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null)
            .Where(n => n!.Equals(GatewayAssembly, StringComparison.Ordinal))
            .ToList();

        offenders.ShouldBeEmpty(
            $"{ConfigurationAssembly} must depend only downward. It referenced [{string.Join(", ", offenders)}]. " +
            "Configuration is a leaf: it may use Abstractions, Domain, Providers.Core and " +
            "Telemetry.Abstractions, but never the gateway runtime. If a config type needs something " +
            "from the gateway, the shared contract belongs in an abstractions project - that is how " +
            "BuiltInArchetypes and AgentDescriptorValidator were resolved during the extraction.");
    }

    /// <summary>
    /// Every assembly the configuration project references must sit below it (AC2, stated positively).
    ///
    /// <para>
    /// Naming the permitted set rather than only forbidding the gateway means a future upward
    /// dependency on a <em>different</em> sibling fails too. Forbidding one name would let the next
    /// coupling through, which is the reactive-allow-list failure mode #2481 records.
    /// </para>
    /// </summary>
    [Fact]
    public void ConfigurationProject_ReferencesOnlyDownwardBotNexusAssemblies()
    {
        var permitted = new HashSet<string>(StringComparer.Ordinal)
        {
            "BotNexus.Domain",
            "BotNexus.Gateway.Abstractions",
            "BotNexus.Gateway.Telemetry.Abstractions",
            // Arrives transitively through Gateway.Abstractions, which references it directly.
            // Contracts itself depends only on Agent.Core and Domain, so it sits below the
            // configuration project and does not re-introduce an upward edge.
            "BotNexus.Gateway.Contracts",
            // #2646: the SQLite-backed config store. Persistence.Sqlite has NO project references of
            // its own - it is a true leaf wrapping Microsoft.Data.Sqlite - so depending on it cannot
            // introduce an upward edge. Gateway.Sessions and the conversation store reach it the same
            // way, which is also why the config store reuses their additive-migration mechanism
            // rather than inventing a second one.
            "BotNexus.Persistence.Sqlite",
            "BotNexus.Agent.Providers.Core",
            "BotNexus.Agent.Providers.Copilot",
        };

        var offenders = ConfigAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("BotNexus.", StringComparison.Ordinal))
            .Where(n => !permitted.Contains(n))
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            $"{ConfigurationAssembly} referenced BotNexus assemblies outside its permitted downward " +
            $"set: [{string.Join(", ", offenders)}]. Permitted: [{string.Join(", ", permitted.OrderBy(p => p, StringComparer.Ordinal))}]. " +
            "Adding a reference here is a deliberate widening of the configuration project's " +
            "dependency surface and should be justified in the PR, not absorbed silently.");
    }
}
