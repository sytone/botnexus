using BotNexus.Core.Abstractions;
using BotNexus.Core.Extensions;
using BotNexus.Cron.Actions;
using BotNexus.Gateway;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Tests.Unit.Tests;

public class SystemActionRegistryTests
{
    [Fact]
    public void Register_StoresActionByName_AndGetReturnsItCaseInsensitively()
    {
        var registry = new SystemActionRegistry();
        var action = new TestSystemAction("Health-Audit", "Runs health checks");

        registry.Register(action);

        registry.Get("health-audit").Should().BeSameAs(action);
    }

    [Fact]
    public void GetAll_ReturnsAllRegisteredActions()
    {
        var registry = new SystemActionRegistry();
        var first = new TestSystemAction("check-updates", "Checks updates");
        var second = new TestSystemAction("health-audit", "Runs health checks");

        registry.Register(second);
        registry.Register(first);

        registry.GetAll().Select(action => action.Name)
            .Should()
            .ContainInOrder("check-updates", "health-audit");
    }

    [Fact]
    public void AddBotNexusCore_RegistersSystemActionRegistryAsSingleton()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        services.AddBotNexusCore(config);

        using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<ISystemActionRegistry>();
        var second = provider.GetRequiredService<ISystemActionRegistry>();

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void AddBotNexus_RegistersBuiltInSystemActions()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        services.AddBotNexus(config);

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ISystemAction) &&
            descriptor.ImplementationType == typeof(CheckUpdatesAction));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ISystemAction) &&
            descriptor.ImplementationType == typeof(HealthAuditAction));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ISystemAction) &&
            descriptor.ImplementationType == typeof(ExtensionScanAction));
    }

    private sealed class TestSystemAction(string name, string description) : ISystemAction
    {
        public string Name { get; } = name;
        public string Description { get; } = description;

        public Task<string> ExecuteAsync(CancellationToken cancellationToken = default)
            => Task.FromResult("ok");
    }
}
