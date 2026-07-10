using BotNexus.Gateway.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Shouldly;

namespace BotNexus.Gateway.Telemetry.Tests;

/// <summary>
/// Tests for the <c>AddBotNexusTelemetry</c> DI wiring shape.
/// </summary>
public sealed class TelemetryServiceCollectionExtensionsTests
{
    private static IConfiguration BuildConfig(params (string Key, string Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v =>
                new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
    }

    [Fact]
    public void AddBotNexusTelemetry_RegistersIMetrics()
    {
        var services = new ServiceCollection();
        services.AddBotNexusTelemetry(BuildConfig());

        using var provider = services.BuildServiceProvider();
        provider.GetService<IMetrics>().ShouldNotBeNull();
    }

    [Fact]
    public void AddBotNexusTelemetry_DefaultEnabled_WiresMeterProvider()
    {
        var services = new ServiceCollection();
        services.AddBotNexusTelemetry(BuildConfig());

        using var provider = services.BuildServiceProvider();
        provider.GetService<MeterProvider>().ShouldNotBeNull();
        provider.GetService<TracerProvider>().ShouldNotBeNull();
    }

    [Fact]
    public void AddBotNexusTelemetry_ExplicitEnabledTrue_WiresMeterProvider()
    {
        var services = new ServiceCollection();
        services.AddBotNexusTelemetry(BuildConfig(("telemetry:Enabled", "true")));

        using var provider = services.BuildServiceProvider();
        provider.GetService<MeterProvider>().ShouldNotBeNull();
    }

    [Fact]
    public void AddBotNexusTelemetry_Disabled_StillRegistersIMetrics()
    {
        var services = new ServiceCollection();
        services.AddBotNexusTelemetry(BuildConfig(("telemetry:Enabled", "false")));

        using var provider = services.BuildServiceProvider();
        provider.GetService<IMetrics>().ShouldNotBeNull();
    }

    [Fact]
    public void AddBotNexusTelemetry_Disabled_DoesNotWireMeterProvider()
    {
        var services = new ServiceCollection();
        services.AddBotNexusTelemetry(BuildConfig(("telemetry:Enabled", "false")));

        using var provider = services.BuildServiceProvider();
        provider.GetService<MeterProvider>().ShouldBeNull();
        provider.GetService<TracerProvider>().ShouldBeNull();
    }

    [Fact]
    public void AddBotNexusTelemetry_ReturnsSameCollection()
    {
        var services = new ServiceCollection();
        var result = services.AddBotNexusTelemetry(BuildConfig());
        result.ShouldBeSameAs(services);
    }

    [Fact]
    public void AddBotNexusTelemetry_NullServices_Throws()
    {
        IServiceCollection services = null!;
        Should.Throw<ArgumentNullException>(() => services.AddBotNexusTelemetry(BuildConfig()));
    }

    [Fact]
    public void AddBotNexusTelemetry_NullConfiguration_Throws()
    {
        var services = new ServiceCollection();
        Should.Throw<ArgumentNullException>(() => services.AddBotNexusTelemetry(null!));
    }

    [Fact]
    public void AddBotNexusTelemetry_IMetrics_IsSingleton()
    {
        var services = new ServiceCollection();
        services.AddBotNexusTelemetry(BuildConfig());

        using var provider = services.BuildServiceProvider();
        var a = provider.GetRequiredService<IMetrics>();
        var b = provider.GetRequiredService<IMetrics>();
        a.ShouldBeSameAs(b);
    }
}
