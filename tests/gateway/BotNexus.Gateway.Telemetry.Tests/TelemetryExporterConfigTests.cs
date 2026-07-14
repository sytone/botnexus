using BotNexus.Gateway.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Shouldly;

namespace BotNexus.Gateway.Telemetry.Tests;

/// <summary>
/// Tests for the config-gated OTLP exporter wiring (#1854). These assert configuration
/// binding and DI wiring shape only; they never open a live network connection to a
/// collector. The off-by-default safety contract (exporter <c>none</c> =&gt; zero egress)
/// is verified structurally.
/// </summary>
public sealed class TelemetryExporterConfigTests
{
    private static IConfiguration BuildConfig(params (string Key, string Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v =>
                new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
    }

    // --- Config binding: happy + defaults ---

    [Fact]
    public void ExporterType_DefaultsToNone()
    {
        var config = new TelemetryConfig();
        config.Exporter.Type.ShouldBe(TelemetryExporterType.None);
    }

    [Fact]
    public void Exporter_HasNoDefaultEndpoint()
    {
        // Off-by-default safety contract: never ship a default endpoint.
        var config = new TelemetryConfig();
        config.Exporter.Endpoint.ShouldBeNull();
    }

    [Fact]
    public void ResourceServiceName_DefaultsToBotnexus()
    {
        new ResourceAttributesConfig().ServiceName.ShouldBe("botnexus");
    }

    [Fact]
    public void Protocol_DefaultsToGrpc()
    {
        new ExporterConfig().Protocol.ShouldBe("grpc");
    }

    [Fact]
    public void BindsExporterSection_FromConfiguration()
    {
        var config = BuildConfig(
            ("telemetry:Exporter:Type", "otlp"),
            ("telemetry:Exporter:Endpoint", "http://collector.example:4317"),
            ("telemetry:Exporter:Protocol", "http/protobuf"),
            ("telemetry:Exporter:Headers:Authorization", "Bearer secret-token"),
            ("telemetry:Exporter:Resource:ServiceName", "botnexus"),
            ("telemetry:Exporter:Resource:ServiceInstanceId", "instance-7"),
            ("telemetry:Exporter:Resource:DeploymentEnvironment", "production"));

        var bound = config.GetSection(TelemetryConfig.SectionName).Get<TelemetryConfig>();

        bound.ShouldNotBeNull();
        bound!.Exporter.Type.ShouldBe(TelemetryExporterType.Otlp);
        bound.Exporter.Endpoint.ShouldBe("http://collector.example:4317");
        bound.Exporter.Protocol.ShouldBe("http/protobuf");
        bound.Exporter.Headers["Authorization"].ShouldBe("Bearer secret-token");
        bound.Exporter.Resource.ServiceName.ShouldBe("botnexus");
        bound.Exporter.Resource.ServiceInstanceId.ShouldBe("instance-7");
        bound.Exporter.Resource.DeploymentEnvironment.ShouldBe("production");
    }

    // --- Service instance id: stable per-instance ---

    [Fact]
    public void ResolveServiceInstanceId_GeneratesStableIdWhenUnset()
    {
        var attrs = new ResourceAttributesConfig();
        var first = attrs.ResolveServiceInstanceId();
        var second = attrs.ResolveServiceInstanceId();

        first.ShouldNotBeNullOrWhiteSpace();
        second.ShouldBe(first); // stable across calls so every exported point shares one id
    }

    [Fact]
    public void ResolveServiceInstanceId_HonoursConfiguredId()
    {
        var attrs = new ResourceAttributesConfig { ServiceInstanceId = "explicit-id" };
        attrs.ResolveServiceInstanceId().ShouldBe("explicit-id");
    }

    // --- DI wiring: sad path (none = no exporter, no egress) ---

    [Fact]
    public void ExporterNone_Default_WiresMeterProviderWithoutExporter()
    {
        var services = new ServiceCollection();
        services.AddBotNexusTelemetry(BuildConfig());

        using var provider = services.BuildServiceProvider();

        // MeterProvider is wired (enabled=true default) but no exporter service is registered,
        // so no OTLP connection is ever attempted. Building the provider must not throw or
        // attempt any network egress.
        provider.GetService<MeterProvider>().ShouldNotBeNull();

        var options = provider.GetRequiredService<IOptions<TelemetryConfig>>().Value;
        options.Exporter.Type.ShouldBe(TelemetryExporterType.None);
    }

    // --- DI wiring: happy path (otlp = exporter + resource attrs) ---

    [Fact]
    public void ExporterOtlp_WithEndpoint_BuildsMeterProvider()
    {
        var services = new ServiceCollection();
        services.AddBotNexusTelemetry(BuildConfig(
            ("telemetry:Exporter:Type", "otlp"),
            ("telemetry:Exporter:Endpoint", "http://localhost:4317"),
            ("telemetry:Exporter:Protocol", "grpc"),
            ("telemetry:Exporter:Resource:ServiceInstanceId", "instance-1")));

        using var provider = services.BuildServiceProvider();

        // Assert the provider builds with the OTLP exporter wired. We do NOT flush/export,
        // so no live network call is made.
        provider.GetService<MeterProvider>().ShouldNotBeNull();
        provider.GetService<TracerProvider>().ShouldNotBeNull();

        var options = provider.GetRequiredService<IOptions<TelemetryConfig>>().Value;
        options.Exporter.Type.ShouldBe(TelemetryExporterType.Otlp);
        options.Exporter.Endpoint.ShouldBe("http://localhost:4317");
    }

    [Fact]
    public void ExporterConsole_BuildsMeterProvider()
    {
        var services = new ServiceCollection();
        services.AddBotNexusTelemetry(BuildConfig(
            ("telemetry:Exporter:Type", "console")));

        using var provider = services.BuildServiceProvider();
        provider.GetService<MeterProvider>().ShouldNotBeNull();
    }

    [Fact]
    public void ExporterOtlp_Disabled_DoesNotWireMeterProvider()
    {
        // Even with an OTLP exporter configured, disabling telemetry wins: no provider, no egress.
        var services = new ServiceCollection();
        services.AddBotNexusTelemetry(BuildConfig(
            ("telemetry:Enabled", "false"),
            ("telemetry:Exporter:Type", "otlp"),
            ("telemetry:Exporter:Endpoint", "http://localhost:4317")));

        using var provider = services.BuildServiceProvider();
        provider.GetService<MeterProvider>().ShouldBeNull();
    }

    // --- Secret redaction ---

    [Fact]
    public void DescribeForLogging_RedactsHeaderValues()
    {
        var exporter = new ExporterConfig
        {
            Type = TelemetryExporterType.Otlp,
            Endpoint = "http://localhost:4317",
        };
        exporter.Headers["Authorization"] = "Bearer super-secret-token";
        exporter.Headers["x-api-key"] = "another-secret";

        var described = exporter.DescribeForLogging();

        described.ShouldNotContain("super-secret-token");
        described.ShouldNotContain("another-secret");
        described.ShouldContain("[REDACTED]");
        // Keys are preserved so operators can confirm which headers are set.
        described.ShouldContain("Authorization");
        described.ShouldContain("x-api-key");
    }

    [Fact]
    public void DescribeForLogging_NoHeaders_ReportsNone()
    {
        var described = new ExporterConfig().DescribeForLogging();
        described.ShouldContain("Headers=[(none)]");
    }

    // --- Agent 365 observability export (#1877) ---

    [Fact]
    public void Agent365_DefaultsToDisabled()
    {
        // Off-by-default safety contract: no A365 egress until an operator opts in.
        var config = new TelemetryConfig();
        config.Agent365.Enabled.ShouldBeFalse();
        config.Agent365.Endpoint.ShouldBeNull();
    }

    [Fact]
    public void BindsAgent365Section_FromConfiguration()
    {
        var config = BuildConfig(
            ("telemetry:Agent365:Enabled", "true"),
            ("telemetry:Agent365:Endpoint", "https://agent365.svc.cloud.microsoft/observabilityService/tenants/t/otlp/agents/a/traces?api-version=1"),
            ("telemetry:Agent365:AuthHeaderValue", "Bearer secret-a365-token"),
            ("telemetry:Agent365:Headers:x-custom", "custom-value"),
            ("telemetry:Agent365:Resource:ServiceName", "myagent"));

        var bound = config.GetSection(TelemetryConfig.SectionName).Get<TelemetryConfig>();

        bound.ShouldNotBeNull();
        bound!.Agent365.Enabled.ShouldBeTrue();
        bound.Agent365.Endpoint.ShouldNotBeNull();
        bound.Agent365.Endpoint!.ShouldContain("/traces?api-version=1");
        bound.Agent365.AuthHeaderValue.ShouldBe("Bearer secret-a365-token");
        bound.Agent365.Headers["x-custom"].ShouldBe("custom-value");
        bound.Agent365.Resource.ServiceName.ShouldBe("myagent");
    }

    [Fact]
    public void Agent365_ResolveHeaders_FoldsAuthHeaderValue()
    {
        var a365 = new Agent365ObservabilityConfig { AuthHeaderValue = "Bearer token-1" };
        a365.Headers["x-extra"] = "e";

        var headers = a365.ResolveHeaders();

        headers["Authorization"].ShouldBe("Bearer token-1");
        headers["x-extra"].ShouldBe("e");
    }

    [Fact]
    public void Agent365_ResolveHeaders_ExplicitAuthorizationWins()
    {
        var a365 = new Agent365ObservabilityConfig { AuthHeaderValue = "Bearer convenience" };
        a365.Headers["Authorization"] = "Bearer explicit";

        a365.ResolveHeaders()["Authorization"].ShouldBe("Bearer explicit");
    }

    [Fact]
    public void Agent365_DescribeForLogging_RedactsSecrets()
    {
        var a365 = new Agent365ObservabilityConfig
        {
            Enabled = true,
            Endpoint = "https://agent365.svc.cloud.microsoft/x/traces?api-version=1",
            AuthHeaderValue = "Bearer super-secret-a365",
        };
        a365.Headers["x-api-key"] = "another-secret";

        var described = a365.DescribeForLogging();

        described.ShouldNotContain("super-secret-a365");
        described.ShouldNotContain("another-secret");
        described.ShouldContain("[REDACTED]");
        described.ShouldContain("Authorization");
        described.ShouldContain("x-api-key");
    }

    [Fact]
    public void Agent365_Enabled_WithEndpoint_BuildsTracerProvider()
    {
        var services = new ServiceCollection();
        services.AddBotNexusTelemetry(BuildConfig(
            ("telemetry:Agent365:Enabled", "true"),
            ("telemetry:Agent365:Endpoint", "https://agent365.svc.cloud.microsoft/observabilityService/tenants/t/otlp/agents/a/traces?api-version=1"),
            ("telemetry:Agent365:AuthHeaderValue", "Bearer token")));

        using var provider = services.BuildServiceProvider();

        // Provider builds with the Agent 365 OTLP exporter wired. We never flush/export,
        // so no live network call is made.
        provider.GetService<TracerProvider>().ShouldNotBeNull();
    }

    [Fact]
    public void Agent365_Disabled_DoesNotBlockTracerProvider()
    {
        // Default (disabled) A365 must not prevent the standard TracerProvider from wiring.
        var services = new ServiceCollection();
        services.AddBotNexusTelemetry(BuildConfig());

        using var provider = services.BuildServiceProvider();
        provider.GetService<TracerProvider>().ShouldNotBeNull();
    }

    [Fact]
    public void Agent365_EnabledWithoutEndpoint_StillBuildsProvider_NoEgress()
    {
        // Enabled but no endpoint => nothing wired for A365, no throw, no egress.
        var services = new ServiceCollection();
        services.AddBotNexusTelemetry(BuildConfig(
            ("telemetry:Agent365:Enabled", "true")));

        using var provider = services.BuildServiceProvider();
        provider.GetService<TracerProvider>().ShouldNotBeNull();
    }

    [Fact]
    public void Agent365_Disabled_WhenTelemetryDisabled_NoProvider()
    {
        // Disabling telemetry wins even with A365 configured: no provider, no egress.
        var services = new ServiceCollection();
        services.AddBotNexusTelemetry(BuildConfig(
            ("telemetry:Enabled", "false"),
            ("telemetry:Agent365:Enabled", "true"),
            ("telemetry:Agent365:Endpoint", "https://agent365.svc.cloud.microsoft/x/traces?api-version=1")));

        using var provider = services.BuildServiceProvider();
        provider.GetService<TracerProvider>().ShouldBeNull();
    }
}
