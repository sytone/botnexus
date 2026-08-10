using BotNexus.Agent.Core.Tools;
using BotNexus.Extensions.Mcp.Transport;

namespace BotNexus.Extensions.Mcp.Tests;

/// <summary>
/// Coverage for issue #2892 at the MCP stdio spawn seam. <c>StdioMcpTransport</c> wrote configured
/// <c>env</c> entries straight into <c>ProcessStartInfo.Environment</c>, duplicating the exec tool's
/// ad-hoc merge: the caller dictionary's own comparer decided collisions, so the platform casing
/// rule was re-derived per site instead of being owned in one place. The transport now routes
/// through the shared <see cref="ProcessEnvironment"/> helper, with its <c>${env:NAME}</c>
/// placeholder resolution riding along as the merge's value projection rather than as a second loop.
/// <para>
/// The transport-level tests assert on the environment block the transport actually hands to
/// <c>Process.Start</c>, which is the child's environment - not on an internal copy.
/// </para>
/// </summary>
[Collection("EnvironmentTests")]
public class StdioTransportEnvKeyCasingTests
{
    /// <summary>
    /// AC2 - on Windows the differently-cased override must leave exactly one entry for the
    /// logical variable, carrying the caller's value.
    /// </summary>
    [Fact]
    public void Merge_WindowsRule_ResolvedOverride_ReplacesInheritedEntry()
    {
        var target = new Dictionary<string, string?>(StringComparer.Ordinal) { ["MCP_PROBE"] = "inherited" };

        ProcessEnvironment.Merge(
            target,
            new Dictionary<string, string> { ["mcp_probe"] = "overridden" },
            StringComparer.OrdinalIgnoreCase,
            StdioMcpTransport.ResolveEnvValue);

        target.Count.ShouldBe(1);
        target.Values.Single().ShouldBe("overridden");
    }

    /// <summary>AC3 - POSIX keeps both spellings as genuinely distinct variables.</summary>
    [Fact]
    public void Merge_PosixRule_ResolvedOverride_KeepsBothEntries()
    {
        var target = new Dictionary<string, string?>(StringComparer.Ordinal) { ["MCP_PROBE"] = "inherited" };

        ProcessEnvironment.Merge(
            target,
            new Dictionary<string, string> { ["mcp_probe"] = "overridden" },
            StringComparer.Ordinal,
            StdioMcpTransport.ResolveEnvValue);

        target.Count.ShouldBe(2);
        target["MCP_PROBE"].ShouldBe("inherited");
        target["mcp_probe"].ShouldBe("overridden");
    }

    /// <summary>
    /// Routing through the shared helper must not cost the transport its <c>${env:NAME}</c>
    /// placeholder resolution - that behaviour is why the merge accepts a value projection.
    /// </summary>
    [Fact]
    public void BuildStartInfo_StillResolvesEnvPlaceholderSyntax()
    {
        Environment.SetEnvironmentVariable("MCP_CASING_SOURCE", "resolved-value");
        try
        {
            var transport = new StdioMcpTransport(
                "dotnet",
                ["--info"],
                new Dictionary<string, string> { ["MCP_CASING_TARGET"] = "${env:MCP_CASING_SOURCE}" });

            var startInfo = transport.BuildStartInfo();

            startInfo.Environment["MCP_CASING_TARGET"].ShouldBe("resolved-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCP_CASING_SOURCE", null);
        }
    }

    /// <summary>
    /// The real spawn path. A differently-cased override must resolve against the inherited
    /// variable under the host platform's rule in the block that is handed to <c>Process.Start</c>.
    /// </summary>
    [Fact]
    public void BuildStartInfo_DifferentlyCasedOverride_AppliesPlatformRuleToChildBlock()
    {
        const string inherited = "BN2892_MCP_PROBE";
        const string overrideKey = "bn2892_mcp_probe";

        Environment.SetEnvironmentVariable(inherited, "inherited");
        try
        {
            var transport = new StdioMcpTransport(
                "dotnet",
                ["--info"],
                new Dictionary<string, string> { [overrideKey] = "overridden" });

            var startInfo = transport.BuildStartInfo();

            var entries = startInfo.Environment
                .Where(pair => pair.Key.StartsWith("BN2892_MCP_PROBE", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (OperatingSystem.IsWindows())
            {
                entries.Count.ShouldBe(1,
                    "Windows environment blocks are case-insensitive: a differently-cased override " +
                    "must replace the inherited entry, not coexist with it (#2892).");
                entries[0].Value.ShouldBe("overridden");
            }
            else
            {
                entries.Count.ShouldBe(2,
                    "POSIX environments are case-sensitive: both spellings are distinct variables.");
                entries.Single(e => e.Key == inherited).Value.ShouldBe("inherited");
                entries.Single(e => e.Key == overrideKey).Value.ShouldBe("overridden");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(inherited, null);
        }
    }

    /// <summary>
    /// Guards the merge against becoming a wholesale replacement: variables the config never
    /// mentioned must still be inherited by the child when <c>inheritEnv</c> is on.
    /// </summary>
    [Fact]
    public void BuildStartInfo_EnvOverride_LeavesUnrelatedInheritedVariablesIntact()
    {
        const string unrelated = "BN2892_MCP_UNRELATED";
        Environment.SetEnvironmentVariable(unrelated, "kept");
        try
        {
            var transport = new StdioMcpTransport(
                "dotnet",
                ["--info"],
                new Dictionary<string, string> { ["BN2892_MCP_OTHER"] = "x" });

            transport.BuildStartInfo().Environment[unrelated].ShouldBe("kept");
        }
        finally
        {
            Environment.SetEnvironmentVariable(unrelated, null);
        }
    }

    /// <summary>
    /// <c>inheritEnv: false</c> must still win over the merge: the child sees only what the
    /// config named, so centralising the merge cannot silently reopen the inherited block.
    /// </summary>
    [Fact]
    public void BuildStartInfo_InheritEnvFalse_ChildSeesOnlyConfiguredVariables()
    {
        const string unrelated = "BN2892_MCP_ISOLATED";
        Environment.SetEnvironmentVariable(unrelated, "leaked");
        try
        {
            var transport = new StdioMcpTransport(
                "dotnet",
                ["--info"],
                new Dictionary<string, string> { ["BN2892_MCP_ONLY"] = "x" },
                workingDirectory: null,
                inheritEnv: false);

            var environment = transport.BuildStartInfo().Environment;

            environment.ShouldNotContainKey(unrelated);
            environment["BN2892_MCP_ONLY"].ShouldBe("x");
        }
        finally
        {
            Environment.SetEnvironmentVariable(unrelated, null);
        }
    }
}
