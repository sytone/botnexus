using System.Text.RegularExpressions;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the Phase 5 / F-6 (part 1) contract:
/// sub-agent detection is the responsibility of the typed
/// <see cref="AgentKind"/> property on <see cref="AgentDescriptor"/>, NOT of
/// substring-matching <c>::subagent::</c> inside a <see cref="BotNexus.Domain.Primitives.SessionId"/>.
/// </summary>
/// <remarks>
/// <para>
/// The substring-based predicate <c>SessionId.IsSubAgent</c> is preserved for
/// back-compat (legacy persisted sessions and a deliberate defense-in-depth
/// callsite in the isolation strategy), but no NEW production code may rely on
/// it. The allowlist below pins exactly which files are permitted to reference
/// <c>SessionId.IsSubAgent</c>; every other use must route through
/// <c>descriptor.Kind == AgentKind.SubAgent</c> instead.
/// </para>
/// <para>
/// Pattern follows the multi-model-critique-approved fence shape from PR #549 / PR #550:
/// (a) compound check (the allowlist is explicit and small),
/// (b) vacuity self-test (proves the regex catches the canonical violation shape),
/// (c) synthetic-violation self-test (proves the regex fires when violated),
/// (d) synthetic-clean self-test (proves the regex doesn't fire on legitimate code).
/// </para>
/// <para>
/// Deferred to follow-up PRs (in <c>plan.md</c> Phase 5 follow-ups):
/// migrating <c>GatewayHost.ResolveSessionType</c> and <c>SessionsController.GetHistory</c>
/// off the substring predicate (those need broader session-creation / read-path changes)
/// and removing the <c>"::subagent::"</c> literal depth calculation in
/// <c>DefaultSubAgentManager</c> (needs a typed <c>ParentSessionId</c> chain on
/// <c>Session</c>). All three callsites are explicitly allowlisted here with a comment.
/// </para>
/// </remarks>
public sealed class AgentKindArchitectureTests
{
    /// <summary>
    /// Files allowed to reference <c>SessionId.IsSubAgent</c>. Every entry here must
    /// have a justification — adding a new allowlist entry without one is the same
    /// shape of bug the fence exists to prevent. Paths are repo-relative under <c>src/</c>.
    /// </summary>
    private static readonly (string RepoRelativePath, string Reason)[] s_substringAllowlist =
    {
        ("domain/BotNexus.Domain/Primitives/SessionId.cs",
            "Declaration site of IsSubAgent. The predicate itself is allowed to mention its own name."),

        ("gateway/BotNexus.Gateway.Sessions/SessionStoreBase.cs",
            "Legacy read-path bucketing in InferSessionType: required to assign SessionType correctly " +
            "when loading sessions persisted before SessionType was reliably saved on every store. " +
            "Removing this would orphan legacy session rows."),

        ("gateway/BotNexus.Gateway/Isolation/InProcessIsolationStrategy.cs",
            "Defense-in-depth OR-gate combined with descriptor.Kind == AgentKind.SubAgent. " +
            "Ensures the spawn-tool gate fails CLOSED if a future path registers a sub-agent " +
            "descriptor without going through DefaultSubAgentManager.SpawnAsync."),

        ("gateway/BotNexus.Gateway/GatewayHost.cs",
            "DEFERRED (sytone/botnexus#554): ResolveSessionType still infers SessionType from the " +
            "SessionId substring during session creation. Tracked for migration to descriptor.Kind + " +
            "explicit SessionType on the InboundMessage handler path."),

        ("gateway/BotNexus.Gateway.Api/Controllers/SessionsController.cs",
            "DEFERRED (sytone/botnexus#555): Read-side filter switches to session.SessionType == AgentSubAgent " +
            "in a follow-up PR — this PR scopes the production write-side gate only."),
    };

    [Fact]
    public void AgentDescriptor_HasKindPropertyOfTypeAgentKind()
    {
        // Reflection pin: prevents a regression that renames or retypes the property without updating
        // the rest of the fence. AgentKind is the single source of truth for the sub-agent species
        // discriminator; if it ever stops being a property of AgentDescriptor of type AgentKind,
        // the production callsites in InProcessIsolationStrategy and DefaultSubAgentManager
        // become silently broken.
        var descriptorType = typeof(AgentDescriptor);
        var kindProperty = descriptorType.GetProperty("Kind");

        kindProperty.ShouldNotBeNull(
            "AgentDescriptor.Kind property is missing. Phase 5 / F-6 (PR introducing AgentKind) " +
            "requires this property to exist as the typed sub-agent discriminator.");
        kindProperty.PropertyType.ShouldBe(typeof(AgentKind),
            $"AgentDescriptor.Kind must be of type AgentKind (was {kindProperty.PropertyType.Name}).");
    }

    [Fact]
    public void NoNewProductionCodeUsesSessionIdSubstringForSubAgentDetection()
    {
        var srcRoot = FindSourceRoot();
        var allowlistFullPaths = s_substringAllowlist
            .Select(entry => NormalizePath(Path.Combine(srcRoot, entry.RepoRelativePath)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var violations = new List<string>();
        foreach (var file in EnumerateProductionCsFiles(srcRoot))
        {
            if (allowlistFullPaths.Contains(NormalizePath(file)))
                continue;

            var source = File.ReadAllText(file);
            if (ContainsBannedShape(source))
            {
                violations.Add($"  {Path.GetRelativePath(srcRoot, file)}");
            }
        }

        violations.ShouldBeEmpty(
            "Production code is using SessionId.IsSubAgent for sub-agent detection. " +
            "Phase 5 / F-6 (part 1) replaces this substring-based heuristic with the typed " +
            "AgentDescriptor.Kind property. New callsites must route through " +
            "descriptor.Kind == AgentKind.SubAgent. If you genuinely need the substring " +
            "check (e.g., a new legacy-data read-path), add the file to s_substringAllowlist " +
            "with a justification.\n" +
            "Violations:\n" + string.Join("\n", violations) + "\n" +
            $"Current allowlist:\n{string.Join("\n", s_substringAllowlist.Select(e => $"  - {e.RepoRelativePath}: {e.Reason}"))}");
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticViolation()
    {
        const string syntheticViolation = """
            public void DoSomething(AgentExecutionContext context)
            {
                if (context.SessionId.IsSubAgent)
                {
                    skipSpawnTools = true;
                }
            }
            """;
        ContainsBannedShape(syntheticViolation).ShouldBeTrue(
            "Vacuity guard: the fence regex must match the canonical violation shape " +
            "(SessionId.IsSubAgent on a SessionId-typed expression). If this assertion fails, " +
            "the fence has been weakened so far that it cannot catch the F-6 regression.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnLegitimateCode()
    {
        // Legitimate sub-agent detection uses descriptor.Kind — no SessionId substring ops.
        const string syntheticClean = """
            public void DoSomething(AgentDescriptor descriptor, AgentExecutionContext context)
            {
                var isSubAgent = descriptor.Kind == AgentKind.SubAgent;
                if (isSubAgent)
                {
                    skipSpawnTools = true;
                }
            }
            """;
        ContainsBannedShape(syntheticClean).ShouldBeFalse(
            "False-positive guard: the fence must not flag the canonical Kind-based detection. " +
            "If this assertion fails, the fence is over-broad and will force authors to disable " +
            "it instead of fixing real bugs.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnUnrelatedSubAgentReferences()
    {
        // The fence targets SessionId.IsSubAgent specifically — not every property named IsSubAgent.
        // E.g. Blazor's CurrentAgent?.SessionType == "agent-subagent" pattern in AgentPanel.razor
        // is unrelated to the SessionId substring concern.
        const string syntheticClean = """
            private bool IsSubAgent => CurrentAgent?.SessionType == "agent-subagent";
            private bool IsSubAgentActive(SubAgentInfo sub) => sub.SubAgentId == _activeId;
            """;
        ContainsBannedShape(syntheticClean).ShouldBeFalse(
            "False-positive guard: the fence must not flag unrelated IsSubAgent identifiers " +
            "(properties on view models, parameter names, etc.). It must specifically target " +
            "the SessionId.IsSubAgent property access on a SessionId-typed expression.");
    }

    private static bool ContainsBannedShape(string source)
    {
        // Strip C# comments first so docstrings and inline comments that legitimately
        // MENTION the predicate by name (e.g., "see also SessionId.IsSubAgent") don't
        // false-positive the fence. The signal we care about is actual code that
        // depends on the predicate at runtime, not prose explaining the design.
        var stripped = StripComments(source);

        // Banned: any access to .IsSubAgent on something that looks like a SessionId expression.
        // We approximate "SessionId expression" via common variable/property names that hold one,
        // and the explicit type-name prefix for static-style or full-qualifier accesses.
        // This intentionally OVER-INCLUDES on variable-name heuristics so an unguarded check
        // is more likely to trip the fence than to slip past it.
        var patterns = new[]
        {
            // someThing.SessionId.IsSubAgent / context.SessionId.IsSubAgent / session.SessionId.IsSubAgent
            @"\bSessionId\s*\.\s*IsSubAgent\b",
            // sessionId.IsSubAgent — a local of type SessionId named sessionId / sid / childSessionId
            @"\b(sessionId|sid|childSessionId|parentSessionId)\s*\.\s*IsSubAgent\b",
        };
        return patterns.Any(p => Regex.IsMatch(stripped, p, RegexOptions.IgnoreCase));
    }

    /// <summary>
    /// Removes single-line (<c>//</c>, <c>///</c>) and block (<c>/* … */</c>) C# comments.
    /// Naive but sufficient for fence-scan purposes (does not need to handle every
    /// pathological string-literal edge case — false positives in fence checks are
    /// acceptable, false negatives are not).
    /// </summary>
    private static string StripComments(string source)
    {
        var noBlockComments = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var noLineComments = Regex.Replace(noBlockComments, @"//[^\r\n]*", string.Empty);
        return noLineComments;
    }

    private static IEnumerable<string> EnumerateProductionCsFiles(string srcRoot)
    {
        return Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

    private static string FindSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }
        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        var srcRoot = Path.Combine(current.FullName, "src");
        Directory.Exists(srcRoot).ShouldBeTrue("Expected src/ under " + current.FullName);
        return srcRoot;
    }
}
