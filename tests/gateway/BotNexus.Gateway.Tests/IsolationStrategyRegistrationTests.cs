using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Isolation;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Gateway.Tests;

public sealed class IsolationStrategyRegistrationTests
{
    [Fact]
    public void AddBotNexusGateway_RegistersAllBuiltInIsolationStrategies()
    {
        var services = new ServiceCollection();

        services.AddBotNexusGateway();

        var strategyImplementations = services
            .Where(d => d.ServiceType == typeof(IIsolationStrategy))
            .Select(d => d.ImplementationType)
            .ToList();

        strategyImplementations.ShouldContain(typeof(InProcessIsolationStrategy));
        strategyImplementations.ShouldContain(typeof(SandboxIsolationStrategy));
        strategyImplementations.ShouldContain(typeof(ContainerIsolationStrategy));
        strategyImplementations.ShouldContain(typeof(RemoteIsolationStrategy));
    }

    [Theory]
    [InlineData("sandbox")]
    [InlineData("container")]
    [InlineData("remote")]
    public async Task StubStrategies_CreateAsync_ThrowsNotSupported(string strategyName)
    {
        IIsolationStrategy strategy = strategyName switch
        {
            "sandbox" => new SandboxIsolationStrategy(),
            "container" => new ContainerIsolationStrategy(),
            _ => new RemoteIsolationStrategy()
        };

        var descriptor = new BotNexus.Gateway.Abstractions.Models.AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "model",
            ApiProvider = "provider",
            IsolationStrategy = strategyName
        };

        var context = new BotNexus.Gateway.Abstractions.Models.AgentExecutionContext
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1")
        };

        Func<Task> act = () => strategy.CreateAsync(descriptor, context);

        var ex = await act.ShouldThrowAsync<NotSupportedException>();
        ex.Message.ShouldContain(strategyName);
        ex.Message.ShouldContain("not yet implemented");
    }

    [Theory]
    [InlineData("sandbox")]
    [InlineData("container")]
    [InlineData("remote")]
    public void StubStrategies_Name_ReturnsExpectedValue(string strategyName)
    {
        IIsolationStrategy strategy = strategyName switch
        {
            "sandbox" => new SandboxIsolationStrategy(),
            "container" => new ContainerIsolationStrategy(),
            _ => new RemoteIsolationStrategy()
        };

        strategy.Name.ShouldBe(strategyName);
    }
}
