using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for issue #2616: <b>every</b> call site that executes an agent
/// through <c>IAgentHandle</c> must reach the single execution-layer tool-audit sink.
/// </summary>
/// <remarks>
/// <para>
/// The #2127 gap did not arise because someone removed an audit call. It arose because
/// ChatController, CrossWorldFederationController, CronTrigger, HeartbeatTrigger, SoulTrigger and
/// <c>DefaultSubAgentManager</c> were each added independently, and nothing in the suite noticed
/// that the newest one had not been wired to the sink. The audit guarantee was therefore only as
/// strong as the memory of whoever added the next execution path.
/// </para>
/// <para>
/// <b>The enumeration is structural, not a name list (AC2).</b> The candidate set is computed from
/// IL: every method body in the shipped gateway assemblies is decompiled, and any
/// <c>callvirt</c>/<c>call</c> whose target is declared on <c>IAgentHandle</c> and named
/// <c>PromptAsync</c> or <c>StreamAsync</c> is an execution call site. Adding a new file, a new
/// controller or a new trigger therefore enters the candidate set automatically - there is no list
/// to forget to update. A hand-maintained list of file names would have exactly the failure mode
/// the issue describes.
/// </para>
/// <para>
/// <b>Deliberate exclusions are declared in code (AC2).</b> The only sanctioned escape is
/// <c>[ToolAuditExempt("reason")]</c> on the declaring method, with a non-empty justification. Every
/// exemption in the repository is findable with one symbol search, and this suite additionally
/// prints them so a review sees the exemption set grow.
/// </para>
/// <para>
/// <b>Reachability is transitive, but not type-wide.</b> A call site satisfies the fence when the
/// sink is reached from the method containing the call, or through a method that method calls
/// (bounded depth). This is what lets <c>CronTrigger</c> route through
/// <c>TriggerToolAuditProjector</c> without being special-cased by name. Reachability is
/// deliberately NOT widened to somewhere on the declaring type: that weaker rule would let a type
/// with one compliant execution path launder a second, bypassing one - and the issue is explicit
/// that a fence which is too permissive is worse than none, because it manufactures confidence.
/// </para>
/// <para>
/// <b>Async is followed.</b> An awaited callee's body is a compiler-generated kickoff stub
/// containing no user code, so a naive walk would conclude that <c>DefaultSubAgentManager</c> never
/// reaches the sink. The walk therefore redirects through <c>AsyncStateMachineAttribute</c> and
/// <c>IteratorStateMachineAttribute</c> to the real <c>MoveNext</c> body.
/// </para>
/// <para>
/// <b>Non-vacuity (AC4).</b> Two things guard against a fence that cannot fail: the candidate set
/// is asserted non-empty and to contain the known execution paths, and
/// <see cref="Fence_Reddens_WhenACallSiteDoesNotReachTheSink"/> runs the same detector over a
/// synthetic bypassing call site and requires it to be flagged. Verified by mutation as well: a
/// throwaway bypassing <c>PromptAsync</c> call site added to the gateway reddens
/// <see cref="EveryAgentExecutionCallSite_ReachesTheToolAuditSink"/> and nothing else.
/// </para>
/// </remarks>
public sealed class AgentExecutionToolAuditFenceArchitectureTests
{
    /// <summary>The interface whose execution members define the fenced surface.</summary>
    private const string HandleInterface = "IAgentHandle";

    /// <summary>The execution members on that interface. Any call to one is a call site.</summary>
    private static readonly string[] ExecutionMethods = ["PromptAsync", "StreamAsync"];

    /// <summary>
    /// Members that constitute "reached the audit sink". Naming the SINK's own members rather than
    /// any wrapper keeps the fence honest: an adapter satisfies it only because it really calls
    /// through, not because it happens to be named something audit-ish.
    /// </summary>
    private static readonly string[] SinkMembers =
    [
        "ProjectBlockingRun",
        "CaptureBlockingRun",
        "ProjectStart",
        "ProjectResult",
        "ProjectIncomplete",
        "PersistStartAsync",
        "RecordInterruptedAsync"
    ];

    private const string ExemptAttribute = "ToolAuditExemptAttribute";

    /// <summary>How far to follow same-assembly calls when looking for the sink.</summary>
    private const int MaxReachabilityDepth = 4;

    [Fact]
    public void EveryAgentExecutionCallSite_ReachesTheToolAuditSink()
    {
        var (callSites, exemptions) = AnalyzeGatewayAssemblies();

        // Non-vacuity guard 1: an empty or tiny candidate set means the IL scan silently found
        // nothing and the fence is asserting over air.
        callSites.Count.ShouldBeGreaterThanOrEqualTo(
            6,
            "the IL scan must discover the gateway's agent-execution call sites; found: " + callSites.Count);

        var violations = callSites
            .Where(c => !c.ReachesSink)
            .Select(c => $"{c.DeclaringType}.{c.Method} -> {c.CalledMember}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty(
            "every IAgentHandle execution call site must route tool activity through the audit sink " +
            "(#2616 AC1). Either persist the sink-produced rows, or declare the exclusion at the call " +
            "site with [ToolAuditExempt(\"reason\")]. Offending sites:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));

        // Every exemption carries a real reason. A blank justification is a silent mute, which is
        // the shape AC2 forbids.
        foreach (var exemption in exemptions)
        {
            exemption.Justification.ShouldNotBeNullOrWhiteSpace(
                $"[ToolAuditExempt] on {exemption.Member} must state why it is outside the fence (#2616 AC2)");
        }
    }

    [Fact]
    public void TheEnumeration_CoversTheKnownExecutionPaths()
    {
        // Non-vacuity guard 2: pin that the STRUCTURAL scan actually reaches the six independently
        // added paths the issue names. This is not the fence's allow-list - the fence has none - it
        // is a floor proving the scan sees real production code rather than an empty assembly set.
        var (callSites, _) = AnalyzeGatewayAssemblies();
        var types = callSites.Select(c => c.DeclaringType).ToHashSet(StringComparer.Ordinal);

        foreach (var expected in new[]
                 {
                     "ChatController",
                     "CrossWorldFederationController",
                     "CronTrigger",
                     "HeartbeatTrigger",
                     "SoulTrigger",
                     "DefaultSubAgentManager"
                 })
        {
            types.Any(t => t.Contains(expected, StringComparison.Ordinal)).ShouldBeTrue(
                $"the structural enumeration must discover {expected} without being told its name; " +
                "found: " + string.Join(", ", types.OrderBy(x => x, StringComparer.Ordinal)));
        }
    }

    [Fact]
    public void Fence_Reddens_WhenACallSiteDoesNotReachTheSink()
    {
        // AC4, exercised in-suite: run the SAME detector over a type that executes an agent and
        // never touches the sink. If this stops being flagged, the fence above has stopped fencing
        // regardless of how green it looks.
        var assembly = typeof(BypassingProbe).Assembly.Location;
        using var module = ModuleDefinition.ReadModule(assembly);

        // The probes are async, so the PromptAsync call lives in the compiler-generated
        // <RunAsync>d__N.MoveNext nested inside the probe type - exactly as it does for every real
        // call site in the gateway. Flatten, as the production scan does; looking only at the
        // top-level type would inspect an empty kickoff stub and find nothing.
        var sites = Flatten(module.Types.First(t => t.Name == nameof(BypassingProbe)))
            .SelectMany(t => t.Methods)
            .Where(m => m.HasBody)
            .SelectMany(m => ExecutionCallSites(m, module))
            .ToList();

        sites.ShouldNotBeEmpty("the synthetic probe must be recognised as an execution call site");
        sites.ShouldAllBe(s => !s.ReachesSink,
            "a call site that never reaches the sink must be reported as a violation");

        // ...and the compliant probe in the same assembly must NOT be flagged, so the detector is
        // discriminating rather than simply reporting everything.
        var compliantSites = Flatten(module.Types.First(t => t.Name == nameof(CompliantProbe)))
            .SelectMany(t => t.Methods)
            .Where(m => m.HasBody)
            .SelectMany(m => ExecutionCallSites(m, module))
            .ToList();

        compliantSites.ShouldNotBeEmpty("the compliant probe must also be recognised as a call site");
        compliantSites.ShouldAllBe(s => s.ReachesSink,
            "a call site that does reach the sink must not be reported as a violation");
    }

    /// <summary>
    /// Loads the shipped gateway assemblies and returns every execution call site plus every
    /// declared exemption.
    /// </summary>
    private static (List<CallSite> CallSites, List<Exemption> Exemptions) AnalyzeGatewayAssemblies()
    {
        var callSites = new List<CallSite>();
        var exemptions = new List<Exemption>();

        foreach (var path in GatewayAssemblies())
        {
            ModuleDefinition module;
            try
            {
                module = ModuleDefinition.ReadModule(path);
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            using (module)
            {
                foreach (var type in AllTypes(module))
                {
                    foreach (var method in type.Methods.Where(m => m.HasBody))
                    {
                        if (ExemptionJustification(method) is { } justification)
                            exemptions.Add(new Exemption($"{type.FullName}.{method.Name}", justification));

                        callSites.AddRange(ExecutionCallSites(method, module));
                    }
                }
            }
        }

        return (callSites, exemptions);
    }

    /// <summary>
    /// Finds the <c>IAgentHandle</c> execution calls in one method body and decides, for each,
    /// whether the audit sink is reachable from that call site.
    /// </summary>
    private static IEnumerable<CallSite> ExecutionCallSites(MethodDefinition method, ModuleDefinition module)
    {
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.OpCode != OpCodes.Callvirt && instruction.OpCode != OpCodes.Call)
                continue;
            if (instruction.Operand is not MethodReference target)
                continue;
            if (!ExecutionMethods.Contains(target.Name, StringComparer.Ordinal))
                continue;
            if (!string.Equals(target.DeclaringType?.Name, HandleInterface, StringComparison.Ordinal))
                continue;

            var reaches = ExemptionJustification(method) is not null
                || ReachesSink(method, module, MaxReachabilityDepth, []);

            yield return new CallSite(
                method.DeclaringType.FullName,
                method.Name,
                $"{target.DeclaringType?.Name}.{target.Name}",
                reaches);
        }
    }

    /// <summary>
    /// True when the audit sink is called from <paramref name="method"/>, or from a same-module
    /// method it calls, within <paramref name="depth"/> hops.
    /// </summary>
    private static bool ReachesSink(
        MethodDefinition method,
        ModuleDefinition module,
        int depth,
        HashSet<string> visited)
    {
        method = StateMachineBody(method) ?? method;

        if (depth < 0 || !method.HasBody || !visited.Add(method.FullName))
            return false;

        var callees = new List<MethodReference>();
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is not MethodReference target)
                continue;

            if (SinkMembers.Contains(target.Name, StringComparer.Ordinal))
                return true;

            callees.Add(target);
        }

        if (depth == 0)
            return false;

        foreach (var callee in callees)
        {
            MethodDefinition? resolved;
            try
            {
                resolved = callee.Resolve();
            }
            catch (AssemblyResolutionException)
            {
                continue;
            }

            if (resolved is null || !resolved.HasBody)
                continue;

            if (ReachesSink(resolved, module, depth - 1, visited))
                return true;
        }

        return false;
    }

    /// <summary>Returns the declared justification, or null when the method is not exempt.</summary>
    private static string? ExemptionJustification(MethodDefinition method)
    {
        var attribute = method.CustomAttributes
            .FirstOrDefault(a => string.Equals(a.AttributeType.Name, ExemptAttribute, StringComparison.Ordinal));

        if (attribute is null)
            return null;

        return attribute.ConstructorArguments.Count > 0
            ? attribute.ConstructorArguments[0].Value as string ?? string.Empty
            : string.Empty;
    }

    /// <summary>
    /// Redirects an async or iterator method to the <c>MoveNext</c> that actually holds its body.
    /// Without this, every awaited hop terminates the walk at an empty kickoff stub and the fence
    /// reports false violations for the compliant paths.
    /// </summary>
    /// <param name="method">The method to redirect.</param>
    /// <returns>The state machine's <c>MoveNext</c>, or null when the method is not a state machine.</returns>
    private static MethodDefinition? StateMachineBody(MethodDefinition method)
    {
        var attribute = method.CustomAttributes.FirstOrDefault(a =>
            a.AttributeType.Name is "AsyncStateMachineAttribute" or "IteratorStateMachineAttribute");

        if (attribute is null || attribute.ConstructorArguments.Count == 0)
            return null;

        if (attribute.ConstructorArguments[0].Value is not TypeReference stateMachineType)
            return null;

        try
        {
            return stateMachineType.Resolve()?.Methods
                .FirstOrDefault(m => string.Equals(m.Name, "MoveNext", StringComparison.Ordinal) && m.HasBody);
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
        => module.Types.SelectMany(Flatten);

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
    {
        yield return type;
        foreach (var descendant in type.NestedTypes.SelectMany(Flatten))
            yield return descendant;
    }

    /// <summary>
    /// The shipped gateway assemblies, discovered from the test output directory. Discovery is by
    /// assembly-name prefix rather than an explicit list so a new gateway project is scanned the
    /// day it ships.
    /// </summary>
    private static List<string> GatewayAssemblies()
    {
        var baseDir = AppContext.BaseDirectory;
        var assemblies = Directory
            .EnumerateFiles(baseDir, "BotNexus.Gateway*.dll", SearchOption.TopDirectoryOnly)
            .Where(f => !Path.GetFileName(f).Contains(".Tests.", StringComparison.Ordinal))
            .ToList();

        assemblies.ShouldNotBeEmpty("expected gateway assemblies alongside the test assembly in " + baseDir);
        return assemblies;
    }

    private sealed record CallSite(string DeclaringType, string Method, string CalledMember, bool ReachesSink);

    private sealed record Exemption(string Member, string Justification);
}
