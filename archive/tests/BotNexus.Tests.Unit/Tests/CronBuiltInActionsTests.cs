using System.Runtime.Loader;
using BotNexus.Cron.Actions;
using BotNexus.Core.Extensions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BotNexus.Tests.Unit.Tests;

public sealed class CronBuiltInActionsTests
{
    [Fact]
    public async Task CheckUpdatesAction_ReturnsExpectedPrefix()
    {
        var action = new CheckUpdatesAction();

        var result = await action.ExecuteAsync();

        result.Should().Contain("[check-updates]");
    }

    [Fact]
    public async Task HealthAuditAction_WithoutHealthService_ReturnsNotRegisteredMessage()
    {
        var action = new HealthAuditAction();

        var result = await action.ExecuteAsync();

        result.Should().Be("[health-audit] Health check service is not registered.");
    }

    [Fact]
    public async Task HealthAuditAction_WithHealthService_ReportsChecks()
    {
        var provider = new ServiceCollection()
            .AddLogging()
            .AddHealthChecks()
            .AddCheck("db", () => HealthCheckResult.Healthy())
            .Services
            .BuildServiceProvider();
        var health = provider.GetRequiredService<HealthCheckService>();
        var action = new HealthAuditAction(health);

        var result = await action.ExecuteAsync();

        result.Should().Contain("[health-audit] Overall status:");
        result.Should().Contain("db: Healthy");
    }

    [Fact]
    public async Task ExtensionScanAction_WithoutRegistrations_ReturnsNoRegistrationsMessage()
    {
        var action = new ExtensionScanAction();

        var result = await action.ExecuteAsync();

        result.Should().Be("[extension-scan] No extension services are currently registered.");
    }

    [Fact]
    public async Task ExtensionScanAction_WithRegistrations_GroupsAndIncludesLoadContextCount()
    {
        var context = new AssemblyLoadContext("cron-test", isCollectible: true);
        try
        {
            var registrations = new[]
            {
                new ExtensionServiceRegistration(typeof(IDisposable), "alpha"),
                new ExtensionServiceRegistration(typeof(IDisposable), "beta"),
                new ExtensionServiceRegistration(typeof(IAsyncDisposable), "gamma")
            };
            var action = new ExtensionScanAction(registrations, new ExtensionLoadContextStore([context]));

            var result = await action.ExecuteAsync();

            result.Should().Contain("[extension-scan] Registered extension services: 3");
            result.Should().Contain("IDisposable: alpha, beta");
            result.Should().Contain("IAsyncDisposable: gamma");
            result.Should().Contain("Load contexts: 1");
        }
        finally
        {
            context.Unload();
        }
    }
}
