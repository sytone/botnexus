using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Gateway.Tests.Extensions;

/// <summary>
/// Locks the constructability decision used by
/// <see cref="AssemblyLoadContextExtensionLoader.PruneUnconstructableExtensionServices"/>.
///
/// Extension tool/contributor implementations are resolved as sets at startup
/// (<c>IEnumerable&lt;IAgentToolContributor&gt;</c>, <c>IEnumerable&lt;IAgentTool&gt;</c>), and DI set
/// resolution is all-or-nothing — a single implementation the container cannot activate aborts host
/// startup, surfacing only as a generic health-check timeout. Regression #2366 activated
/// <c>DebugToolContributor</c> (bare <c>string dbPath</c> ctor) and <c>DataStoreTool</c>
/// (unregistered <c>IDataStoreBackend</c> ctor param), taking the gateway down on boot. The prune
/// pass removes exactly these before they are resolved; these tests pin the keep/prune boundary.
/// </summary>
public sealed class ExtensionServicePruneTests
{
    private interface IRegisteredDependency;

    private interface IUnregisteredDependency;

    private sealed class ParameterlessContributor : IAgentToolContributor
    {
        public Task<AgentToolContribution> ContributeAsync(AgentToolContributionContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentToolContribution([]));
    }

    // Shape of DebugToolContributor: a bare `string dbPath` with no default the container cannot supply.
    private sealed class StringCtorContributor : IAgentToolContributor
    {
        public StringCtorContributor(string dbPath, IRegisteredDependency? optional = null) => _ = (dbPath, optional);

        public Task<AgentToolContribution> ContributeAsync(AgentToolContributionContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentToolContribution([]));
    }

    // Shape of DataStoreTool: an interface parameter that is never registered as a host service.
    private sealed class UnregisteredInterfaceCtorContributor : IAgentToolContributor
    {
        public UnregisteredInterfaceCtorContributor(IUnregisteredDependency dependency) => _ = dependency;

        public Task<AgentToolContribution> ContributeAsync(AgentToolContributionContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentToolContribution([]));
    }

    private sealed class RegisteredInterfaceCtorContributor : IAgentToolContributor
    {
        public RegisteredInterfaceCtorContributor(IRegisteredDependency dependency) => _ = dependency;

        public Task<AgentToolContribution> ContributeAsync(AgentToolContributionContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentToolContribution([]));
    }

    private sealed class OptionalOnlyContributor : IAgentToolContributor
    {
        public OptionalOnlyContributor(string label = "default", IUnregisteredDependency? dependency = null) => _ = (label, dependency);

        public Task<AgentToolContribution> ContributeAsync(AgentToolContributionContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentToolContribution([]));
    }

    private sealed class ServiceProviderCtorContributor : IAgentToolContributor
    {
        public ServiceProviderCtorContributor(IServiceProvider services) => _ = services;

        public Task<AgentToolContribution> ContributeAsync(AgentToolContributionContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentToolContribution([]));
    }

    // The greediest ctor is unsatisfiable (unregistered interface) but a lesser ctor is satisfiable,
    // mirroring how the container falls back to the greediest *resolvable* constructor.
    private sealed class MultiCtorContributor : IAgentToolContributor
    {
        public MultiCtorContributor()
        {
        }

        public MultiCtorContributor(IUnregisteredDependency dependency) => _ = dependency;

        public Task<AgentToolContribution> ContributeAsync(AgentToolContributionContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentToolContribution([]));
    }

    private static IServiceProviderIsService BuildProbe()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRegisteredDependency>(new RegisteredDependency());
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceProviderIsService>();
    }

    private sealed class RegisteredDependency : IRegisteredDependency;

    [Theory]
    [InlineData(typeof(ParameterlessContributor))]
    [InlineData(typeof(RegisteredInterfaceCtorContributor))]
    [InlineData(typeof(OptionalOnlyContributor))]
    [InlineData(typeof(ServiceProviderCtorContributor))]
    [InlineData(typeof(MultiCtorContributor))]
    public void HasContainerSatisfiableConstructor_ReturnsTrue_ForActivatableImplementations(Type implementation)
    {
        var probe = BuildProbe();

        AssemblyLoadContextExtensionLoader.HasContainerSatisfiableConstructor(implementation, probe).ShouldBeTrue();
    }

    [Theory]
    [InlineData(typeof(StringCtorContributor))]
    [InlineData(typeof(UnregisteredInterfaceCtorContributor))]
    public void HasContainerSatisfiableConstructor_ReturnsFalse_ForUnactivatableImplementations(Type implementation)
    {
        var probe = BuildProbe();

        AssemblyLoadContextExtensionLoader.HasContainerSatisfiableConstructor(implementation, probe).ShouldBeFalse();
    }
}
