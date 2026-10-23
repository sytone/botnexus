namespace BotNexus.Extensions.Plugins.Api.Tests;

/// <summary>Pins the composed lifecycle using fake transport boundaries, never a gateway process.</summary>
public sealed class PluginMcpLifecycleTests
{
    [Fact]
    public async Task ComposedLifecycle_ActivationTeardownAndCompensation_AreAwaited() =>
        await PluginMcpLifecycleScenarios.RunAllAsync();
}
