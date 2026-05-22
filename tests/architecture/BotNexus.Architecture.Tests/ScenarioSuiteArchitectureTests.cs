using System.Reflection;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Scenarios.Harness;
using NetArchTest.Rules;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions for the scenario test suite. These rules structurally enforce
/// the channel-agnostic / production-shaped conventions documented in <c>tests/scenarios/AGENTS.md</c>
/// (plan §10.6). If a future change pulls a channel-extension reference into the scenarios project,
/// or reaches past the harness DSL into the gateway's <c>IServiceProvider</c>, the build fails
/// before the change lands. This is the antidote to slop accumulating in the suite over time.
/// </summary>
public sealed class ScenarioSuiteArchitectureTests
{
    private static readonly Assembly HarnessAssembly = typeof(VirtualChannelAdapter).Assembly;
    private static readonly Assembly ScenarioTestsAssembly =
        Assembly.Load("BotNexus.Scenarios.Tests");

    /// <summary>
    /// Scenario tests must remain channel-agnostic: a scenario that references any
    /// <c>BotNexus.Extensions.Channels.*</c> assembly is a tight coupling to a single
    /// transport and defeats the channel-conformance role of the suite.
    /// </summary>
    [Fact]
    public void ScenarioTests_DoNotReferenceAnyChannelExtension()
    {
        var referenced = ScenarioTestsAssembly
            .GetReferencedAssemblies()
            .Select(name => name.Name!)
            .Where(name => name.StartsWith("BotNexus.Extensions.Channels.", StringComparison.Ordinal))
            .ToArray();

        referenced.ShouldBeEmpty(
            "Scenario tests must remain channel-agnostic. Found channel-extension references: " +
            string.Join(", ", referenced));
    }

    /// <summary>
    /// The scenario harness is consumed by the test project and (later) per-channel conformance
    /// projects, so it must not pull in any channel-extension dependency that would force
    /// adopters of the harness to also drag in unrelated transports.
    /// </summary>
    [Fact]
    public void ScenarioHarness_DoesNotReferenceAnyChannelExtension()
    {
        var referenced = HarnessAssembly
            .GetReferencedAssemblies()
            .Select(name => name.Name!)
            .Where(name => name.StartsWith("BotNexus.Extensions.Channels.", StringComparison.Ordinal))
            .ToArray();

        referenced.ShouldBeEmpty(
            "BotNexus.Scenarios.Harness must remain channel-agnostic. Found references: " +
            string.Join(", ", referenced));
    }

    /// <summary>
    /// <see cref="VirtualChannelAdapter"/> must remain a real <see cref="IChannelAdapter"/>
    /// implementation. If a future refactor breaks that contract (e.g. by inheriting from a
    /// stub that no longer satisfies the interface), every scenario silently loses fidelity.
    /// </summary>
    [Fact]
    public void VirtualChannelAdapter_ImplementsIChannelAdapter()
    {
        typeof(IChannelAdapter).IsAssignableFrom(typeof(VirtualChannelAdapter))
            .ShouldBeTrue($"{nameof(VirtualChannelAdapter)} must implement {nameof(IChannelAdapter)}.");
    }

    /// <summary>
    /// Scenario tests must drive the platform through the harness DSL (the virtual adapter
    /// and supporting helpers), never by reaching into <see cref="IServiceProvider"/> directly.
    /// Direct DI access from a scenario test is a smell — the DSL exposes the right level of
    /// abstraction. If the DSL is missing a verb, add the verb; don't reach past it.
    /// </summary>
    [Fact]
    public void ScenarioTests_DoNotDependOnIServiceProvider()
    {
        var result = Types.InAssembly(ScenarioTestsAssembly)
            .Should()
            .NotHaveDependencyOn("Microsoft.Extensions.DependencyInjection")
            .And()
            .NotHaveDependencyOn("System.IServiceProvider")
            .GetResult();

        var failing = result.FailingTypeNames ?? [];
        failing.ShouldBeEmpty(
            "Scenario tests must drive the platform through the harness DSL, not by resolving from DI directly. " +
            "Offending types: " + string.Join(", ", failing));
    }

    /// <summary>
    /// The harness's public surface must not leak DI primitives (<see cref="IServiceProvider"/>,
    /// <see cref="IServiceCollection"/>, hosting types). A future <c>VirtualWorld</c> harness will
    /// build its own DI graph internally — that's fine — but it must expose only typed verbs
    /// (e.g. <c>Adapter</c>, <c>AsUser</c>, <c>WaitForReplyAsync</c>) so that scenarios stay at
    /// spec level and do not devolve into service-location tests.
    /// </summary>
    [Fact]
    public void ScenarioHarness_PublicSurface_DoesNotLeakDiPrimitives()
    {
        var disallowed = new[]
        {
            "System.IServiceProvider",
            "Microsoft.Extensions.DependencyInjection.IServiceCollection",
            "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory",
            "Microsoft.Extensions.DependencyInjection.IServiceScope",
            "Microsoft.Extensions.Hosting.IHost",
            "Microsoft.Extensions.Hosting.IHostBuilder",
            "Microsoft.Extensions.Hosting.IHostedService",
        };

        var leaks = new List<string>();

        foreach (var type in HarnessAssembly.GetExportedTypes())
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (disallowed.Contains(prop.PropertyType.FullName))
                    leaks.Add($"{type.FullName}.{prop.Name} : {prop.PropertyType.FullName}");
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (disallowed.Contains(method.ReturnType.FullName))
                    leaks.Add($"{type.FullName}.{method.Name}() : {method.ReturnType.FullName}");
                foreach (var parameter in method.GetParameters())
                {
                    if (disallowed.Contains(parameter.ParameterType.FullName))
                        leaks.Add($"{type.FullName}.{method.Name}({parameter.Name}) : {parameter.ParameterType.FullName}");
                }
            }

            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var parameter in ctor.GetParameters())
                {
                    if (disallowed.Contains(parameter.ParameterType.FullName))
                        leaks.Add($"{type.FullName}.ctor({parameter.Name}) : {parameter.ParameterType.FullName}");
                }
            }
        }

        leaks.ShouldBeEmpty(
            "BotNexus.Scenarios.Harness must not expose DI primitives on its public surface. " +
            "Found: " + string.Join("; ", leaks));
    }
}
