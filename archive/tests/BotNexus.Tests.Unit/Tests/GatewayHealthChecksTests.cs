using BotNexus.Core.Configuration;
using BotNexus.Core.Extensions;
using BotNexus.Gateway.HealthChecks;
using BotNexus.Providers.Base;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BotNexus.Tests.Unit.Tests;

public sealed class GatewayHealthChecksTests
{
    [Fact]
    public async Task ProviderRegistration_NoProvidersConfigured_IsHealthy()
    {
        var registry = new ProviderRegistry();
        var options = Options.Create(new BotNexusConfig { Providers = new ProvidersConfig() });
        var check = new ProviderRegistrationHealthCheck(registry, options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("No providers configured");
    }

    [Fact]
    public async Task ProviderReadiness_NoProvidersConfigured_IsHealthy()
    {
        var registry = new ProviderRegistry();
        var options = Options.Create(new BotNexusConfig { Providers = new ProvidersConfig() });
        var check = new ProviderReadinessHealthCheck(registry, options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("No providers configured");
    }

    [Fact]
    public async Task ProviderReadiness_ConfiguredProviderMissing_IsUnhealthy()
    {
        var providers = new ProvidersConfig
        {
            ["copilot"] = new ProviderConfig { Auth = "oauth" }
        };
        var options = Options.Create(new BotNexusConfig { Providers = providers });
        var check = new ProviderReadinessHealthCheck(new ProviderRegistry(), options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("copilot");
    }

    [Fact]
    public async Task ExtensionLoader_WithOnlyWarnings_IsHealthy()
    {
        var report = new ExtensionLoadReport
        {
            Results =
            [
                new ExtensionLoadResult("providers", "missing", false, "Extension folder not found", CountsAsFailure: false)
            ],
            LoadedCount = 0,
            FailedCount = 0,
            WarningCount = 1,
            Completed = true
        };
        var check = new ExtensionLoaderHealthCheck(report);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("warnings");
        result.Data["warnings"].Should().Be(1);
    }
}
