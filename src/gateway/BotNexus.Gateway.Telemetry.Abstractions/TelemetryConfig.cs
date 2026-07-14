using System.Collections.Generic;

namespace BotNexus.Gateway.Telemetry;

/// <summary>
/// Binds the <c>telemetry</c> configuration section. Covers both the in-process
/// metrics/tracing foundation (<see cref="Enabled"/>) and the optional, off-by-default
/// remote exporter (<see cref="Exporter"/>) that ships instruments to an external
/// OpenTelemetry collector over OTLP.
/// </summary>
/// <remarks>
/// This type is deliberately OpenTelemetry-SDK-free so it can live in the abstractions
/// assembly and be referenced by projects (Gateway, CLI) that must not pull the OTel SDK
/// into their DI graph. The SDK wiring that consumes this config lives only in the
/// composition-root <c>BotNexus.Gateway.Telemetry</c> project.
/// </remarks>
public sealed class TelemetryConfig
{
    /// <summary>Configuration section name under the root config.</summary>
    public const string SectionName = "telemetry";

    /// <summary>
    /// Whether the in-process OpenTelemetry metrics/tracing plane is active. Defaults to
    /// <see langword="true"/>. When <see langword="false"/>, <c>AddBotNexusTelemetry</c>
    /// still registers <see cref="IMetrics"/> (so call sites resolve) but does not wire the
    /// OpenTelemetry MeterProvider/TracerProvider.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Remote exporter configuration. Defaults to <see cref="TelemetryExporterType.None"/>
    /// (no egress): the off-by-default safety contract means a fresh install never opens an
    /// outbound OTLP connection until an operator explicitly opts in with an endpoint.
    /// </summary>
    public ExporterConfig Exporter { get; set; } = new();

    /// <summary>
    /// Agent 365 observability export configuration. Defaults to disabled (the off-by-default
    /// safety contract): a fresh install never routes telemetry to Agent 365 until an operator
    /// explicitly opts in with an endpoint and auth header. When enabled, the canonical
    /// BotNexus turn/tool-call/provider-invocation spans are shipped to the Agent 365
    /// observability ingestion endpoint over raw OTLP/HTTP alongside (not instead of) the
    /// generic <see cref="Exporter"/> target.
    /// </summary>
    public Agent365ObservabilityConfig Agent365 { get; set; } = new();
}

/// <summary>
/// Configures direct OTLP/HTTP export of BotNexus telemetry spans to the Microsoft
/// Agent 365 observability ingestion endpoint. This is a raw OTLP target: it takes no
/// dependency on any <c>Microsoft.Agents.A365.Observability</c> SDK. Auth is config-driven
/// (a bearer token supplied via <see cref="AuthHeaderValue"/>, typically minted by an
/// operator's MSAL/token-acquisition process out of band).
/// </summary>
/// <remarks>
/// The Agent 365 traces route is documented at
/// https://learn.microsoft.com/en-us/microsoft-agent-365/developer/direct-open-telemetry-integration.
/// The typical S2S endpoint shape is
/// <c>https://agent365.svc.cloud.microsoft/observabilityService/tenants/{tenantId}/otlp/agents/{agentId}/traces?api-version=1</c>
/// and callers set an <c>Authorization: Bearer &lt;token&gt;</c> header. This type keeps the
/// full endpoint operator-supplied so BotNexus does not hard-code the (evolving) route shape.
/// </remarks>
public sealed class Agent365ObservabilityConfig
{
    /// <summary>
    /// Whether Agent 365 observability export is active. Defaults to <see langword="false"/>
    /// (the off-by-default safety contract): no telemetry is ever routed to Agent 365 until an
    /// operator explicitly enables it and supplies an <see cref="Endpoint"/>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The Agent 365 observability OTLP/HTTP traces endpoint (the fully-qualified
    /// <c>.../otlp/agents/{agentId}/traces?api-version=1</c> URL). Required when
    /// <see cref="Enabled"/> is <see langword="true"/>; ignored otherwise. Deliberately has no
    /// default value so no endpoint is ever shipped.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// The value of the <c>Authorization</c> header sent on every OTLP request (for example
    /// <c>Bearer eyJ...</c>). Treated as a secret: redacted wherever config is logged. Optional
    /// so operators can also supply auth via <see cref="Headers"/> or an upstream proxy.
    /// </summary>
    public string? AuthHeaderValue { get; set; }

    /// <summary>
    /// Additional OTLP request headers (beyond <c>Authorization</c>). Header values are treated
    /// as secrets and redacted wherever config is logged.
    /// </summary>
    public IDictionary<string, string> Headers { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resource attributes that identify this agent to Agent 365.</summary>
    public ResourceAttributesConfig Resource { get; set; } = new();

    /// <summary>
    /// Returns the effective set of OTLP headers, folding <see cref="AuthHeaderValue"/> into an
    /// <c>Authorization</c> entry when present. Explicit <see cref="Headers"/> win over the
    /// convenience <see cref="AuthHeaderValue"/> if both set <c>Authorization</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> ResolveHeaders()
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(AuthHeaderValue))
        {
            merged["Authorization"] = AuthHeaderValue!;
        }

        foreach (var kvp in Headers)
        {
            merged[kvp.Key] = kvp.Value;
        }

        return merged;
    }

    /// <summary>
    /// Returns a human-readable, secret-safe description of this config suitable for logging or
    /// a config dump. The auth token and all header <em>values</em> are replaced with
    /// <c>[REDACTED]</c> (keys are preserved so operators can confirm which headers are set).
    /// </summary>
    public string DescribeForLogging()
    {
        var headerKeys = ResolveHeaders().Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        var headers = headerKeys.Count == 0
            ? "(none)"
            : string.Join(", ", headerKeys.Select(k => $"{k}=[REDACTED]"));

        return $"Enabled={Enabled}, Endpoint={Endpoint ?? "(unset)"}, " +
               $"Headers=[{headers}], Resource=({Resource.DescribeForLogging()})";
    }
}

/// <summary>
/// Selects which telemetry exporter (if any) <c>AddBotNexusTelemetry</c> attaches to the
/// MeterProvider/TracerProvider.
/// </summary>
public enum TelemetryExporterType
{
    /// <summary>
    /// No exporter. In-process instruments are still recorded and readable via the local
    /// snapshot endpoint, but nothing is sent off-box. This is the default.
    /// </summary>
    None = 0,

    /// <summary>Export over OTLP to the configured <see cref="ExporterConfig.Endpoint"/>.</summary>
    Otlp = 1,

    /// <summary>Write instruments to the process console. Intended for local debugging only.</summary>
    Console = 2,
}

/// <summary>
/// Configures the remote OTLP exporter and the OpenTelemetry resource attributes that
/// identify this process to a downstream collector/aggregator.
/// </summary>
public sealed class ExporterConfig
{
    /// <summary>The gRPC OTLP protocol identifier (default).</summary>
    public const string ProtocolGrpc = "grpc";

    /// <summary>The HTTP/protobuf OTLP protocol identifier.</summary>
    public const string ProtocolHttpProtobuf = "http/protobuf";

    /// <summary>
    /// Which exporter to attach. Defaults to <see cref="TelemetryExporterType.None"/> so a
    /// fresh install produces zero network egress until explicitly configured.
    /// </summary>
    public TelemetryExporterType Type { get; set; } = TelemetryExporterType.None;

    /// <summary>
    /// The OTLP collector endpoint (e.g. <c>http://localhost:4317</c> for grpc or
    /// <c>http://localhost:4318</c> for http/protobuf). Required when <see cref="Type"/> is
    /// <see cref="TelemetryExporterType.Otlp"/>; ignored otherwise. Deliberately has no
    /// default value so no endpoint is ever shipped.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// OTLP wire protocol: <c>grpc</c> (default) or <c>http/protobuf</c>. See
    /// <see cref="ProtocolGrpc"/> / <see cref="ProtocolHttpProtobuf"/>.
    /// </summary>
    public string Protocol { get; set; } = ProtocolGrpc;

    /// <summary>
    /// Optional OTLP request headers (e.g. an auth token for a managed collector). Header
    /// values are treated as secrets: they are redacted wherever config is logged or dumped.
    /// </summary>
    public IDictionary<string, string> Headers { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resource attributes that identify this process to the collector.</summary>
    public ResourceAttributesConfig Resource { get; set; } = new();

    /// <summary>
    /// Returns a human-readable, secret-safe description of this exporter config suitable for
    /// logging or a config dump. Header <em>values</em> are replaced with <c>[REDACTED]</c>
    /// (keys are preserved so operators can confirm which headers are set).
    /// </summary>
    public string DescribeForLogging()
    {
        var headers = Headers.Count == 0
            ? "(none)"
            : string.Join(", ", Headers.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Select(k => $"{k}=[REDACTED]"));

        return $"Type={Type}, Endpoint={Endpoint ?? "(unset)"}, Protocol={Protocol}, " +
               $"Headers=[{headers}], Resource=({Resource.DescribeForLogging()})";
    }
}

/// <summary>
/// OpenTelemetry resource attributes carried on every exported metric/trace so a downstream
/// aggregator can attribute data to a specific service instance.
/// </summary>
public sealed class ResourceAttributesConfig
{
    /// <summary>The default <c>service.name</c> value.</summary>
    public const string DefaultServiceName = "botnexus";

    /// <summary>
    /// The <c>service.name</c> resource attribute. Defaults to <c>botnexus</c>.
    /// </summary>
    public string ServiceName { get; set; } = DefaultServiceName;

    /// <summary>
    /// The <c>service.instance.id</c> resource attribute. When unset, a stable random id is
    /// generated once at startup so a future aggregator can distinguish concurrent instances
    /// even when the operator did not assign explicit ids.
    /// </summary>
    public string? ServiceInstanceId { get; set; }

    /// <summary>
    /// The <c>deployment.environment</c> resource attribute (e.g. <c>production</c>,
    /// <c>staging</c>). Optional; omitted from the resource when unset.
    /// </summary>
    public string? DeploymentEnvironment { get; set; }

    /// <summary>
    /// Returns the effective <see cref="ServiceInstanceId"/>, generating and caching a stable
    /// per-process id when none was configured. The generated id is stable for the lifetime of
    /// this config object (i.e. the process).
    /// </summary>
    public string ResolveServiceInstanceId()
    {
        if (!string.IsNullOrWhiteSpace(ServiceInstanceId))
        {
            return ServiceInstanceId;
        }

        // Generate once and cache so every exported point shares one instance id.
        ServiceInstanceId ??= Guid.NewGuid().ToString();
        return ServiceInstanceId;
    }

    /// <summary>Returns a secret-safe description for logging.</summary>
    public string DescribeForLogging() =>
        $"service.name={ServiceName}, service.instance.id={ServiceInstanceId ?? "(auto)"}, " +
        $"deployment.environment={DeploymentEnvironment ?? "(unset)"}";
}
