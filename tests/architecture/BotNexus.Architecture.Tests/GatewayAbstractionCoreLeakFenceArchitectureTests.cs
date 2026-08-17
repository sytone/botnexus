using System.Reflection;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function pinning the gateway abstraction seam against agent-core leakage (#3040).
///
/// <para>
/// <b>Why this exists.</b> <c>BotNexus.Gateway.Contracts</c> and <c>BotNexus.Gateway.Abstractions</c> are
/// the assemblies every downstream component references precisely so it does <em>not</em> have to depend
/// on the agent implementation. Both published agent-core types straight through their public surface, so
/// the abstraction did not abstract: ten extension projects consume a type declared under <c>src/agent</c>
/// while believing they consume a gateway contract.
/// </para>
///
/// <para>
/// <b>Why it is asserted on IL rather than on source text.</b> The original leak was concealed by
/// <c>using</c> aliases - <c>using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;</c> - so a
/// reader of <c>IAgentHandle</c> saw a gateway-sounding name and any grep for
/// <c>BotNexus.Agent.Core</c> in a signature reported zero. Aliases are a compile-time fiction: they do
/// not survive into metadata. Reflecting over the emitted public members therefore resolves aliases by
/// construction and cannot be defeated the way the original situation was concealed (AC4).
/// <see cref="Fence_DetectsAnAliasConcealedReintroduction"/> proves that claim against a probe type whose
/// leak is declared exclusively through an alias.
/// </para>
///
/// <para>
/// <b>The baseline shrinks, never grows.</b> Removing every leak in one change means retyping
/// <c>IAgentTool</c> across 114 files and ten extension projects, which is not a reviewable diff. The
/// residual leaks are therefore enumerated explicitly and asserted to be <em>exactly</em> that set: a new
/// leak fails the fence, and removing a listed one fails it too until the entry is deleted. A baseline
/// that is only an upper bound rots into permission; this one is an equality, so it can only get smaller.
/// </para>
/// </summary>
public sealed class GatewayAbstractionCoreLeakFenceArchitectureTests
{
    /// <summary>Assemblies whose public surface must not publish agent-core types.</summary>
    private static readonly string[] FencedAssemblies =
    [
        "BotNexus.Gateway.Contracts",
        "BotNexus.Gateway.Abstractions"
    ];

    /// <summary>
    /// The residual, explicitly accepted leaks after the #3040 first slice, as
    /// <c>Assembly|DeclaringType.Member -> LeakedType</c>. This list may only shrink.
    ///
    /// <para>
    /// Two clusters remain, both deliberately out of this slice because neither can be retyped
    /// without a cascade far larger than the message seam this change closes:
    /// </para>
    /// <list type="bullet">
    ///   <item><b><c>IAgentTool</c></b> - the tool-contribution surface. Giving the gateway its own
    ///   tool abstraction means retyping every tool implementation across the extension projects.</item>
    ///   <item><b>provider models</b> (<c>LlmModel</c>, <c>ThinkingLevel</c>) - model and thinking
    ///   resolution genuinely speaks the provider registry's vocabulary; a gateway mirror would need
    ///   its own model catalogue to be meaningful rather than a rename.</item>
    /// </list>
    /// <para>
    /// <c>IAgentHandle.FollowUpAsync</c> is the one message-seam member still on core: it takes an
    /// <c>AgentMessage</c> (an assistant-authored transcript entry), not a <c>UserMessage</c>, so it
    /// belongs to the transcript cluster rather than the user-message cluster this slice retyped.
    /// </para>
    /// </summary>
    private static readonly string[] AcceptedResidualLeaks =
    [
        // IAgentTool cluster. IAgentToolContributor itself is clean: it returns
        // AgentToolContribution, and that record is what carries the IReadOnlyList<IAgentTool>.
        "BotNexus.Gateway.Abstractions|BotNexus.Gateway.Abstractions.Agents.AgentToolContribution..ctor -> BotNexus.Agent.Core.Tools.IAgentTool",
        "BotNexus.Gateway.Abstractions|BotNexus.Gateway.Abstractions.Agents.AgentToolContribution.Deconstruct -> BotNexus.Agent.Core.Tools.IAgentTool",
        "BotNexus.Gateway.Abstractions|BotNexus.Gateway.Abstractions.Agents.AgentToolContribution.Tools -> BotNexus.Agent.Core.Tools.IAgentTool",
        "BotNexus.Gateway.Contracts|BotNexus.Gateway.Abstractions.Agents.IAgentHandleInspector.ResolveTool -> BotNexus.Agent.Core.Tools.IAgentTool",
        "BotNexus.Gateway.Contracts|BotNexus.Gateway.Abstractions.Agents.IAgentToolFactory.CreateTools -> BotNexus.Agent.Core.Tools.IAgentTool",
        "BotNexus.Gateway.Contracts|BotNexus.Gateway.Abstractions.Extensions.CommandExecutionContext.ResolveSessionTool -> BotNexus.Agent.Core.Tools.IAgentTool",

        // Provider-model cluster.
        "BotNexus.Gateway.Contracts|BotNexus.Gateway.Abstractions.Agents.ContextWindowResolver.Resolve -> BotNexus.Agent.Providers.Core.Models.LlmModel",
        "BotNexus.Gateway.Contracts|BotNexus.Gateway.Abstractions.Agents.EffectiveExecutionSettings..ctor -> BotNexus.Agent.Providers.Core.Models.ThinkingLevel",
        "BotNexus.Gateway.Contracts|BotNexus.Gateway.Abstractions.Agents.EffectiveExecutionSettings.Deconstruct -> BotNexus.Agent.Providers.Core.Models.ThinkingLevel",
        "BotNexus.Gateway.Contracts|BotNexus.Gateway.Abstractions.Agents.EffectiveExecutionSettings.Thinking -> BotNexus.Agent.Providers.Core.Models.ThinkingLevel",

        // Transcript-message cluster.
        "BotNexus.Gateway.Contracts|BotNexus.Gateway.Abstractions.Agents.IAgentHandle.FollowUpAsync -> BotNexus.Agent.Core.Types.AgentMessage"
    ];

    /// <summary>
    /// The public surface of the two abstraction assemblies exposes no agent-core type beyond the
    /// explicitly accepted residual set (AC1, AC2).
    /// </summary>
    [Fact]
    public void GatewayAbstractionAssemblies_DoNotPublishAgentCoreTypes()
    {
        var found = FencedAssemblies
            .Select(LoadFenced)
            .SelectMany(a => FindCoreLeaks(a).Select(l => $"{a.GetName().Name}|{l}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var expected = AcceptedResidualLeaks.OrderBy(x => x, StringComparer.Ordinal).ToArray();

        var added = found.Except(expected, StringComparer.Ordinal).ToArray();
        var removed = expected.Except(found, StringComparer.Ordinal).ToArray();

        added.ShouldBeEmpty(
            "A public member of a gateway abstraction assembly now exposes a type declared under " +
            "src/agent. The whole point of these assemblies is that a downstream consumer can " +
            "reference them WITHOUT taking a dependency on the agent implementation; publishing a " +
            "core type here silently re-couples every one of them. Give the gateway its own contract " +
            "type and map to core inside the isolation strategy, which is the layer entitled to know. " +
            "New leaks:" + Environment.NewLine + string.Join(Environment.NewLine, added));

        removed.ShouldBeEmpty(
            "A leak listed in AcceptedResidualLeaks no longer exists - good news. Delete its entry so " +
            "the baseline keeps shrinking; a stale entry is standing permission to reintroduce it. " +
            "Resolved:" + Environment.NewLine + string.Join(Environment.NewLine, removed));
    }

    /// <summary>
    /// The fence resolves <c>using</c> aliases, so an alias-concealed reintroduction is caught (AC4).
    ///
    /// <para>
    /// <see cref="AliasConcealedLeakProbe"/> declares its leaked parameter exclusively through a
    /// <c>using</c> alias, exactly as <c>IAgentHandle</c> did. Nothing in that type's source text
    /// contains the string <c>BotNexus.Agent.Core</c> on the member itself. If the fence were
    /// implemented over source text it would report clean here; because it reads metadata, it does not.
    /// </para>
    /// </summary>
    [Fact]
    public void Fence_DetectsAnAliasConcealedReintroduction()
    {
        var leaks = FindCoreLeaks(typeof(AliasConcealedLeakProbe).Assembly)
            .Where(l => l.StartsWith(typeof(AliasConcealedLeakProbe).FullName!, StringComparison.Ordinal))
            .ToArray();

        leaks.ShouldContain(
            $"{typeof(AliasConcealedLeakProbe).FullName}.LeakThroughAnAlias -> BotNexus.Agent.Core.Types.UserMessage",
            "The fence must see through a using alias. If this fails, the detector has regressed to " +
            "matching source text or namespace strings and would report the original #3040 leak as clean.");
    }

    /// <summary>
    /// The fence is not vacuous: it actually inspects a non-trivial number of public members, so a
    /// detector that silently enumerated nothing cannot read as a pass.
    /// </summary>
    [Fact]
    public void Fence_ActuallyInspectsTheAbstractionSurface()
    {
        foreach (var name in FencedAssemblies)
        {
            var count = LoadFenced(name).GetExportedTypes().Length;
            count.ShouldBeGreaterThan(
                10,
                $"{name} exported only {count} public types. Either the assembly was gutted or the " +
                "fence is inspecting the wrong assembly, in which case its green result means nothing.");
        }
    }

    // ── Detector ─────────────────────────────────────────────────────────

    private static Assembly LoadFenced(string name)
        => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name)
           ?? Assembly.Load(name);

    /// <summary>
    /// Enumerates <c>DeclaringType.Member -> LeakedCoreType</c> for every public member of
    /// <paramref name="assembly"/> whose signature mentions a type declared in an agent assembly.
    /// Operates on metadata, so <c>using</c> aliases are already resolved.
    /// </summary>
    private static IEnumerable<string> FindCoreLeaks(Assembly assembly)
    {
        foreach (var type in assembly.GetExportedTypes())
        {
            foreach (var (member, signatureType) in SignatureTypes(type))
            {
                foreach (var leaked in Flatten(signatureType).Where(IsAgentCoreType).Distinct())
                    yield return $"{type.FullName}.{member} -> {leaked.FullName}";
            }
        }
    }

    private static IEnumerable<(string Member, Type Type)> SignatureTypes(Type type)
    {
        foreach (var baseOrInterface in type.GetInterfaces().Concat(type.BaseType is null ? [] : [type.BaseType]))
            yield return ("<inheritance>", baseOrInterface);

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var property in type.GetProperties(flags))
            yield return (property.Name, property.PropertyType);

        foreach (var field in type.GetFields(flags))
            yield return (field.Name, field.FieldType);

        foreach (var method in type.GetMethods(flags).Where(m => !m.IsSpecialName))
        {
            yield return (method.Name, method.ReturnType);
            foreach (var parameter in method.GetParameters())
                yield return (method.Name, parameter.ParameterType);
        }

        foreach (var ctor in type.GetConstructors(flags))
        {
            foreach (var parameter in ctor.GetParameters())
                yield return (".ctor", parameter.ParameterType);
        }
    }

    /// <summary>Expands generic arguments, arrays and by-ref wrappers so a leak cannot hide inside one.</summary>
    private static IEnumerable<Type> Flatten(Type type)
    {
        if (type.IsByRef || type.IsPointer || type.IsArray)
        {
            var element = type.GetElementType();
            if (element is not null)
            {
                foreach (var inner in Flatten(element))
                    yield return inner;
            }

            yield break;
        }

        yield return type;

        if (!type.IsGenericType)
            yield break;

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var inner in Flatten(argument))
                yield return inner;
        }
    }

    private static bool IsAgentCoreType(Type type)
        => (type.Assembly.GetName().Name ?? string.Empty).StartsWith("BotNexus.Agent.", StringComparison.Ordinal);
}
