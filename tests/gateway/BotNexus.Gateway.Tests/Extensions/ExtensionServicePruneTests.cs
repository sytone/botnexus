using System.IO.Abstractions;
using System.Reflection;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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

    [Fact]
    public void PruneUnconstructableExtensionServices_PreservesUnrelatedFactoryRegistration_ForSameContract()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentToolContributor>(_ => new ParameterlessContributor());
        services.AddSingleton<IAgentToolContributor, StringCtorContributor>();
        var loader = new AssemblyLoadContextExtensionLoader(
            services,
            new HookDispatcher(),
            NullLogger<AssemblyLoadContextExtensionLoader>.Instance,
            new FileSystem());
        var registrationField = typeof(AssemblyLoadContextExtensionLoader).GetField(
            "_registeredExtensionServices",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected extension registration tracking field.");
        var registrations = registrationField.GetValue(loader) as List<(Type Contract, Type Implementation)>
            ?? throw new InvalidOperationException("Expected extension registration tracking list.");
        registrations.Add((typeof(IAgentToolContributor), typeof(StringCtorContributor)));

        var pruned = loader.PruneUnconstructableExtensionServices();

        pruned.ShouldHaveSingleItem();
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(IAgentToolContributor) &&
            descriptor.ImplementationFactory is not null).ShouldBeTrue();
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(IAgentToolContributor) &&
            descriptor.ImplementationType == typeof(StringCtorContributor)).ShouldBeFalse();
    }

    [Fact]
    public void PruneUnconstructableExtensionServices_RemovesTrackedFactoryRegistration_ForRejectedImplementation()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentToolContributor>(_ => new ParameterlessContributor());
        services.AddSingleton<StringCtorContributor>();
        var rejectedFactory = ServiceDescriptor.Singleton(
            typeof(IAgentToolContributor),
            serviceProvider => serviceProvider.GetRequiredService<StringCtorContributor>());
        ((IServiceCollection)services).Add(rejectedFactory);
        var loader = new AssemblyLoadContextExtensionLoader(
            services,
            new HookDispatcher(),
            NullLogger<AssemblyLoadContextExtensionLoader>.Instance,
            new FileSystem());
        var registrationField = typeof(AssemblyLoadContextExtensionLoader).GetField(
            "_registeredExtensionServices",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected extension registration tracking field.");
        var registrations = registrationField.GetValue(loader) as List<(Type Contract, Type Implementation)>
            ?? throw new InvalidOperationException("Expected extension registration tracking list.");
        registrations.Add((typeof(IAgentToolContributor), typeof(StringCtorContributor)));
        var factoryField = typeof(AssemblyLoadContextExtensionLoader).GetField(
            "_extensionFactoryDescriptors",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected extension factory descriptor tracking field.");
        var factoryDescriptors = factoryField.GetValue(loader)
            as Dictionary<(Type Contract, Type Implementation), List<ServiceDescriptor>>
            ?? throw new InvalidOperationException("Expected extension factory descriptor tracking dictionary.");
        factoryDescriptors[(typeof(IAgentToolContributor), typeof(StringCtorContributor))] = [rejectedFactory];

        var pruned = loader.PruneUnconstructableExtensionServices();

        pruned.ShouldHaveSingleItem();
        services.Contains(rejectedFactory).ShouldBeFalse();
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(IAgentToolContributor) &&
            descriptor.ImplementationFactory is not null).ShouldBeTrue();
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(StringCtorContributor)).ShouldBeFalse();
    }
}
