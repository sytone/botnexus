using BotNexus.Gateway.Api.Extensions;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using BotNexus.Agent.Providers.Core.Logging;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.Logging;
using BotNexus.Agent.Providers.Core.Resilience;
using BotNexus.Agent.Providers.Anthropic;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Copilot.Responses;
using BotNexus.Agent.Providers.Copilot.Completions;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using Microsoft.Extensions.Options;
using BotNexus.Agent.Providers.OpenAI;
using BotNexus.Agent.Providers.OpenAICompat;
using BotNexus.Agent.Providers.Copilot;
using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Agent.Providers.GitHubModels;
using BotNexus.Agent.Providers.IntegrationMock;
using BotNexus.Gateway.Models;
using BotNexus.Cron;
using BotNexus.Cron.Extensions;
using BotNexus.Domain.World;
using Microsoft.AspNetCore.Routing;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Trace;
using BotNexus.Gateway.Telemetry;
using Serilog;
using System.Reflection;
using BotNexus.Gateway.Tools;
using BotNexus.Gateway.Webhooks;

const string GatewayCorsPolicy = "GatewayCorsPolicy";

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(BotNexusHome.ResolveDataPath() ?? BotNexusHome.ResolveHomePath(), "logs", "botnexus-bootstrap-.log"),
        rollingInterval: RollingInterval.Day)
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args
});

// Enable running as an OS service (no-op when running interactively)
builder.Host.UseSystemd();
builder.Host.UseWindowsService();

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}",
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Warning)
    .WriteTo.File(
        Path.Combine(BotNexusHome.ResolveDataPath() ?? BotNexusHome.ResolveHomePath(), "logs", "botnexus-.log"),
        rollingInterval: RollingInterval.Hour,
        retainedFileCountLimit: 168),
    preserveStaticLogger: true);

var platformConfigPath = builder.Configuration["BotNexus:ConfigPath"];
var resolvedConfigPath = string.IsNullOrWhiteSpace(platformConfigPath)
    ? PlatformConfigLoader.GetDefaultConfigPath(new System.IO.Abstractions.FileSystem())
    : Path.GetFullPath(platformConfigPath);

// Add config.json to the IConfiguration pipeline so IOptionsMonitor<T> gets reload and extension
// assemblies can bind their own config sections without a separate file-reading path.
// Pre-validate the JSON: a malformed config.json must not prevent startup. ConfigurationManager
// eagerly loads (and re-loads on change) each source, and a parse failure there escapes on a
// background thread which would crash the host. If the file is invalid we skip adding it to the
// pipeline entirely and the gateway runs on defaults until the file is fixed and the host restarts.
if (IsValidJsonFile(resolvedConfigPath))
{
    try
    {
        builder.Configuration.AddJsonFile(resolvedConfigPath, optional: true, reloadOnChange: true);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Failed to add {ConfigPath} to configuration — using defaults. Fix the JSON and restart.", resolvedConfigPath);
    }
}
else if (File.Exists(resolvedConfigPath))
{
    Log.Warning("{ConfigPath} is not valid JSON — using defaults. Fix the JSON and restart to apply it.", resolvedConfigPath);
}

PlatformConfig startupPlatformConfig;
try
{
    startupPlatformConfig = PlatformConfigLoader.Load(resolvedConfigPath, validateOnLoad: false);
}
catch (Exception ex) when (ex is Microsoft.Extensions.Options.OptionsValidationException or System.Text.Json.JsonException)
{
    Log.Warning(ex, "Failed to load platform config from {ConfigPath} — using defaults", resolvedConfigPath);
    startupPlatformConfig = new PlatformConfig();
}

// Telemetry foundation (metrics core + OpenTelemetry SDK wiring): registers the IMetrics
// facade, the botnexus.host.starts smoke counter, and the "BotNexus" meter/tracing scope.
// AddOpenTelemetry() is idempotent, so the tracing block below augments the same builder.
builder.Services.AddBotNexusTelemetry(builder.Configuration);

// Extension telemetry seam (#1852): the shared durable usage-telemetry store plus the
// IExtensionTelemetryFactory that mints per-extension namespaced metrics/usage handles. This
// gives extensions the same telemetry seam the platform core uses (no privileged internal-only
// path): metrics auto-prefixed to botnexus.ext.<id>.*, durable usage isolated to the extension
// id namespace within one shared SQLite file (so extensions never new up their own database).
var usageTelemetryPath = System.IO.Path.Combine(
    BotNexusHome.ResolveDataPath() ?? BotNexusHome.ResolveHomePath(), "data", "usage-telemetry.db");
builder.Services.AddExtensionTelemetry(usageTelemetryPath);

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            .AddSource("BotNexus.Gateway")
            .AddSource("BotNexus.Providers")
            .AddSource("BotNexus.Channels")
            .AddSource("BotNexus.Agents")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();

        var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
        }
    });

// Response compression (#1781): compress dynamic JSON/text responses (Brotli + Gzip).
// BREACH note: gateway is dev-first / loopback-oriented, so the classic
// compression-oracle (CRIME/BREACH) risk is low here; still, responses that reflect
// attacker-influenced input alongside secrets in the same body are a consideration.
// Fastest level is used deliberately to avoid a CPU cliff on hot dynamic paths.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    options.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes
        .Concat(new[] { "application/json", "application/manifest+json" });
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(
    o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(
    o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.AddBotNexusGateway(builder.Configuration);
builder.Services.AddDiagnosticsHardening();
builder.Services.AddProviderHealthCheck();
builder.Services.AddBotNexusCron();
builder.Services.AddPlatformConfiguration(resolvedConfigPath, builder.Configuration);
builder.Services.Configure<CronOptions>(options =>
{
    options.PromptTemplates = startupPlatformConfig.PromptTemplates?
        .ToDictionary(
            pair => pair.Key,
            pair => new ConfiguredPromptTemplate
            {
                Prompt = pair.Value.Prompt,
                Description = pair.Value.Description,
                Defaults = pair.Value.Defaults?.ToDictionary(entry => entry.Key, entry => (string?)entry.Value, StringComparer.OrdinalIgnoreCase),
                Parameters = pair.Value.Parameters?.ToDictionary(
                    entry => entry.Key,
                    entry => new ConfiguredPromptTemplateParameter
                    {
                        Description = entry.Value.Description,
                        Default = entry.Value.Default,
                        Required = entry.Value.Required
                    },
                    StringComparer.OrdinalIgnoreCase)
            },
            StringComparer.OrdinalIgnoreCase);

    var cron = startupPlatformConfig.Cron;
    if (cron is null)
        return;

    options.Enabled = cron.Enabled;
    options.TickIntervalSeconds = cron.TickIntervalSeconds;
    options.Jobs = cron.Jobs?
        .ToDictionary(
            pair => pair.Key,
            pair => new ConfiguredCronJob
            {
                Name = pair.Value.Name,
                Schedule = pair.Value.Schedule,
                ActionType = pair.Value.ActionType,
                AgentId = pair.Value.AgentId,
                Message = pair.Value.Message,
                TemplateName = pair.Value.TemplateName,
                TemplateParameters = pair.Value.TemplateParameters?.ToDictionary(entry => entry.Key, entry => (string?)entry.Value, StringComparer.OrdinalIgnoreCase),
                Model = ResolveCronModel(pair.Value),
                WebhookUrl = pair.Value.WebhookUrl,
                ShellCommand = pair.Value.ShellCommand,
                Enabled = pair.Value.Enabled,
                CreatedBy = pair.Value.CreatedBy,
                Metadata = pair.Value.Metadata?.ToDictionary(entry => entry.Key, entry => (object?)entry.Value)
            },
            StringComparer.OrdinalIgnoreCase);
});

// Webhook stores - SQLite placed in the writable data directory (BOTNEXUS_DATA_DIR) so it works
// when the config directory is mounted read-only; falls back to the config directory locally.
var webhookDataDir = BotNexusHome.ResolveDataPath() ?? System.IO.Path.GetDirectoryName(resolvedConfigPath)!;
var webhookDbPath = System.IO.Path.Combine(webhookDataDir, "webhooks.sqlite");
builder.Services.AddBotNexusWebhooks(webhookDbPath, configuration: builder.Configuration);

// Portal Tools store - SQLite placed in the same writable data directory so user-defined
// tools survive gateway restarts and roam with the user across browsers/devices (#2232).
var toolsDbPath = System.IO.Path.Combine(webhookDataDir, "tools.sqlite");
builder.Services.AddBotNexusTools(toolsDbPath);

static string? ResolveCronModel(CronJobConfig config)
{
    if (!string.IsNullOrWhiteSpace(config.Model))
        return config.Model;

    if (config.Metadata is null)
        return null;

    var metadataModel = config.Metadata
        .FirstOrDefault(pair => string.Equals(pair.Key, "model", StringComparison.OrdinalIgnoreCase))
        .Value;

    return string.IsNullOrWhiteSpace(metadataModel)
        ? null
        : metadataModel;
}

builder.Services.AddExtensionLoading();
builder.Services.AddSignalR(options =>
    SignalRHubLimits.Apply(options, startupPlatformConfig.Gateway?.SignalR));
builder.Services.AddBotNexusGatewayApi();
builder.Services.AddCors(options =>
{
    options.AddPolicy(GatewayCorsPolicy, policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            policy.AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader();
            return;
        }

        var configuredOrigins = startupPlatformConfig.Gateway?.Cors?.AllowedOrigins?
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var allowedOrigins = configuredOrigins is { Length: > 0 }
            ? configuredOrigins
            : ["http://localhost:5005"];

        // Production CORS is intentionally scoped to explicit verbs for least-privilege API exposure.
        policy.WithOrigins(allowedOrigins)
            .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
            .AllowAnyHeader();
    });
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var assembly = typeof(Program).Assembly;
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "BotNexus Gateway",
        Version = assembly.GetName().Version?.ToString() ?? "1.0.0"
    });

    var xmlFile = $"{assembly.GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
        options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
});
builder.Services.AddSingleton<ApiProviderRegistry>();
builder.Services.AddSingleton<ModelRegistry>();
builder.Services.AddSingleton<BuiltInModels>();
builder.Services.AddHttpClient();
builder.Services.AddTransient<ProviderLoggingHandler>(sp =>
{
    // Always wire the gateway's shared SecretRedactor into the handler so that any API key or
    // token that leaks into a request/response body (not just the well-known auth headers) is
    // scrubbed before it is written to the logs. Redaction is applied unconditionally whenever
    // the handler logs; the config flag below only controls whether the handler runs at all.
    var redactor = sp.GetService<ISecretRedactor>();
    return new ProviderLoggingHandler(
        sp.GetRequiredService<ILogger<ProviderLoggingHandler>>(),
        redactor is null ? null : redactor.Redact);
});
builder.Services.AddHttpClient("BotNexus", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
})
.AddHttpMessageHandler(sp =>
    // Outermost handler: retry transient provider transport failures (notably HTTP 421
    // Misdirected Request from Copilot endpoints) on a fresh connection before they are
    // converted into exceptions/empty responses. Benefits every provider that flows through
    // the shared provider HttpClient, including the session compaction summary call.
    new TransientHttpRetryHandler(
        sp.GetService<ILoggerFactory>()?.CreateLogger<TransientHttpRetryHandler>()))
.AddHttpMessageHandler(sp =>
{
    // Conditionally attach the logging handler based on gateway config.
    // The handler checks IsEnabled(Debug) internally — no log entries emitted when debug is off.
    var config = sp.GetService<IOptionsMonitor<PlatformConfig>>()?.CurrentValue;
    if (config?.Gateway?.EnableProviderRequestLogging == true)
        return sp.GetRequiredService<ProviderLoggingHandler>();
    // Return a no-op pass-through handler when logging is disabled.
    return new NoOpDelegatingHandler();
});
builder.Services.AddSingleton<HttpClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return factory.CreateClient("BotNexus");
});
builder.Services.AddSingleton<LlmClient>(serviceProvider =>
{
    var apiProviders = serviceProvider.GetRequiredService<ApiProviderRegistry>();
    var models = serviceProvider.GetRequiredService<ModelRegistry>();
    var httpClient = serviceProvider.GetRequiredService<HttpClient>();
    var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

    apiProviders.Register(new AnthropicProvider(httpClient));
    apiProviders.Register(new CopilotMessagesProvider(httpClient));
    apiProviders.Register(new OpenAICompletionsProvider(httpClient, loggerFactory.CreateLogger<OpenAICompletionsProvider>()));
    apiProviders.Register(new OpenAIResponsesProvider(httpClient, loggerFactory.CreateLogger<OpenAIResponsesProvider>()));

    apiProviders.Register(new CopilotCompletionsProvider(httpClient, loggerFactory.CreateLogger<CopilotCompletionsProvider>()));
    apiProviders.Register(new CopilotResponsesProvider(httpClient, loggerFactory.CreateLogger<CopilotResponsesProvider>()));
    apiProviders.Register(new OpenAICompatProvider(httpClient));
    apiProviders.Register(new IntegrationMockProvider());

    // #1639: resolve the per-provider API endpoint (enterprise vs individual GitHub Copilot
    // host from auth.json) up-front and hand it to RegisterAll so every Copilot model is born
    // with the CORRECT BaseUrl. No downstream consumer patches model.BaseUrl anymore.
    var authManager = serviceProvider.GetRequiredService<GatewayAuthManager>();

    serviceProvider.GetRequiredService<BuiltInModels>().RegisterAll(models, authManager.GetApiEndpoint);
    new IntegrationMockModels().RegisterAll(models);
    GitHubModelsProvider.RegisterModels(models);

    // Dynamic model discovery: overlay live API models onto built-in registry.
    // Discovery is best-effort — failures fall back to built-in models.
    var discoveryClient = new CopilotDiscoveryClient(httpClient);
    var copilotDiscovery = new CopilotModelDiscoveryProvider(
        discoveryClient,
        async ct =>
        {
            var apiKey = await authManager.GetApiKeyAsync("github-copilot", ct);
            var endpoint = authManager.GetApiEndpoint("github-copilot");
            return (apiKey, endpoint);
        },
        loggerFactory.CreateLogger<CopilotModelDiscoveryProvider>());

    var discoveryService = new ModelDiscoveryService(
        models,
        [copilotDiscovery],
        loggerFactory.CreateLogger<ModelDiscoveryService>());
    discoveryService.DiscoverAndRegisterAsync().GetAwaiter().GetResult();

    // Register models from openai-compat providers in config (e.g. Ollama, LM Studio),
    // or any provider with an explicit Api override (e.g. integration-mock).
    var platformConfig = serviceProvider.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue;
    if (platformConfig.Providers is not null)
    {
        foreach (var (providerName, providerConfig) in platformConfig.Providers)
        {
            if (!providerConfig.Enabled)
                continue;

            var apiName = string.IsNullOrWhiteSpace(providerConfig.Api)
                ? "openai-completions"
                : providerConfig.Api!;
            // For openai-completions a BaseUrl is required (the HTTP endpoint). For other
            // apis (e.g. integration-mock) BaseUrl is provider-specific (catalog file path,
            // possibly empty) — skip the BaseUrl gate.
            if (apiName == "openai-completions" && string.IsNullOrWhiteSpace(providerConfig.BaseUrl))
                continue;

            if (providerConfig.Models is { Count: > 0 })
            {
                foreach (var modelId in providerConfig.Models)
                {
                    // PBI6 (#1707): a dynamic (config-declared) model carries a valid capability set
                    // so the agent + conversation pickers offer only valid thinking/context choices.
                    // Explicit declarations win; anything omitted is inferred from the model family.
                    var caps = DynamicModelCapabilities.Infer(
                        modelId,
                        declaredReasoning: providerConfig.Reasoning,
                        declaredExtraHighThinking: providerConfig.SupportsExtraHighThinking,
                        declaredExtendedContext: providerConfig.SupportsExtendedContextWindow);
                    models.Register(providerName, new LlmModel(
                        Id: modelId,
                        Name: modelId,
                        Api: apiName,
                        Provider: providerName,
                        BaseUrl: providerConfig.BaseUrl ?? string.Empty,
                        Reasoning: caps.Reasoning,
                        Input: ["text"],
                        Cost: new ModelCost(0, 0, 0, 0),
                        ContextWindow: providerConfig.ContextWindow ?? 128000,
                        MaxTokens: 32000,
                        SupportsExtraHighThinking: caps.SupportsExtraHighThinking,
                        SupportsExtendedContextWindow: caps.SupportsExtendedContextWindow));
                }
            }
        }
    }

    // Register the model for any agent using a config-defined provider not in BuiltInModels.
    if (platformConfig.Agents is not null && platformConfig.Providers is not null)
    {
        foreach (KeyValuePair<string, AgentDefinitionConfig> agentEntry in platformConfig.Agents)
        {
            var agentConfig = agentEntry.Value;
            if (string.IsNullOrWhiteSpace(agentConfig.Provider) || string.IsNullOrWhiteSpace(agentConfig.Model))
                continue;
            if (!platformConfig.Providers.TryGetValue(agentConfig.Provider, out var agentProvider))
                continue;
            var apiName = string.IsNullOrWhiteSpace(agentProvider.Api)
                ? "openai-completions"
                : agentProvider.Api!;
            if (apiName == "openai-completions" && string.IsNullOrWhiteSpace(agentProvider.BaseUrl))
                continue;
            if (models.GetModel(agentConfig.Provider, agentConfig.Model) is not null)
                continue;

            // PBI6 (#1707): same capability inference for an agent-referenced dynamic model, so a
            // provider that only appears via an agent's model reference still exposes valid pickers.
            var agentModelCaps = DynamicModelCapabilities.Infer(
                agentConfig.Model,
                declaredReasoning: agentProvider.Reasoning,
                declaredExtraHighThinking: agentProvider.SupportsExtraHighThinking,
                declaredExtendedContext: agentProvider.SupportsExtendedContextWindow);
            models.Register(agentConfig.Provider, new LlmModel(
                Id: agentConfig.Model,
                Name: agentConfig.Model,
                Api: apiName,
                Provider: agentConfig.Provider,
                BaseUrl: agentProvider.BaseUrl ?? string.Empty,
                Reasoning: agentModelCaps.Reasoning,
                Input: ["text"],
                Cost: new ModelCost(0, 0, 0, 0),
                ContextWindow: agentProvider.ContextWindow ?? 128000,
                MaxTokens: 32000,
                SupportsExtraHighThinking: agentModelCaps.SupportsExtraHighThinking,
                SupportsExtendedContextWindow: agentModelCaps.SupportsExtendedContextWindow));
        }
    }

    return new LlmClient(apiProviders, models);
});

using (var bootstrapLoggerFactory = new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger, dispose: false))
{
    var extensionLoadResults = await builder.Services.LoadConfiguredExtensionsAsync(startupPlatformConfig, bootstrapLoggerFactory);
    if (extensionLoadResults.Any(result => !result.Success))
    {
        var failed = string.Join(", ", extensionLoadResults.Where(result => !result.Success).Select(result => result.ExtensionId));
        bootstrapLoggerFactory.CreateLogger("BotNexus.Gateway.Extensions")
            .LogWarning("Some extensions failed to load during startup bootstrap: {FailedExtensions}", failed);
    }
}

// Log diagnostics: capture Warning+ entries into an in-memory ring buffer for the log-patterns API.
builder.Services.AddSingleton<BotNexus.Gateway.Diagnostics.LogDiagnosticsRingBuffer>();
builder.Services.AddSingleton<ILoggerProvider, BotNexus.Gateway.Diagnostics.LogDiagnosticsProvider>();

var app = builder.Build();

var platformConfig = app.Services.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue;
var worldDescriptor = WorldDescriptorBuilder.Build(
    platformConfig,
    app.Services.GetRequiredService<IAgentRegistry>(),
    app.Services.GetServices<IIsolationStrategy>());
var worldIdentity = worldDescriptor.Identity;
var listenUrl = platformConfig.Gateway?.ListenUrl;
if (!string.IsNullOrWhiteSpace(listenUrl))
{
    app.Urls.Clear();
    app.Urls.Add(listenUrl);
}

// Extension post-build: endpoint + API contributors
// Extensions serve their own static content (e.g., Blazor WASM at /).
AssemblyLoadContextExtensionLoader.MapExtensionEndpoints(app);

app.UseCors(GatewayCorsPolicy);
// Response compression must run early (right after CORS, before auth/correlation)
// so it wraps API responses. The built-in ResponseCompressionMiddleware skips any
// response that already carries a Content-Encoding header, so precompressed static
// assets served by SignalREndpointContributor (Content-Encoding: br) are NOT
// double-compressed. Accept-Encoding is honoured and Vary: Accept-Encoding is
// emitted automatically by the middleware.
app.UseResponseCompression();
app.UseSerilogRequestLogging();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<GatewayAuthMiddleware>();
app.UseMiddleware<RateLimitingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapGet("/health", async (IServiceProvider sp) =>
{
    // Health endpoint hardening: if the handler cannot execute within 5 seconds,
    // it means the threadpool is exhausted or a deadlock is preventing work scheduling.
    // Return 503 in that case rather than holding the connection open indefinitely.
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var response = await BotNexus.Gateway.Diagnostics.HealthEndpointHelper.ExecuteWithTimeoutAsync(
        () =>
        {
            var tracker = sp.GetService<BotNexus.Gateway.Diagnostics.IActivityTracker>();
            var lastActivity = tracker?.LastActivityUtc;
            var elapsed = tracker?.TimeSinceLastActivity;
            var status = elapsed switch
            {
                { TotalMinutes: >= 10 } => "degraded",
                { TotalMinutes: >= 5 } => "warning",
                _ => "ok"
            };
            return Task.FromResult(new BotNexus.Gateway.Diagnostics.HealthResponse(
                status,
                lastActivity?.ToString("o"),
                elapsed?.TotalSeconds));
        },
        cts.Token);

    if (response.Status == "timeout")
    {
        return Results.Json(
            new { status = "timeout", message = "Health check timed out — possible threadpool exhaustion or deadlock" },
            statusCode: 503);
    }

    return Results.Ok(new
    {
        status = response.Status,
        lastActivity = response.LastActivity,
        inactivitySeconds = response.InactivitySeconds
    });
});
app.MapGet("/api/version", () =>
{
    var assembly = typeof(Program).Assembly;
    var buildTime = File.GetLastWriteTimeUtc(assembly.Location).ToString("yyyyMMddHHmmss");
    var gitHash = GetGitCommitHash();
    return Results.Ok(new { version = buildTime, commit = gitHash });
});
var gatewayStartedAtUtc = DateTimeOffset.UtcNow;
app.MapGet("/api/uptime", () => Results.Ok(new { startedAt = gatewayStartedAtUtc }));
app.MapGet("/api/world", () => Results.Ok(worldDescriptor));

LogGatewayStartup(app, builder.Environment, startupPlatformConfig, worldDescriptor, listenUrl);

// Crash observability (#1901): install the last-chance fault handler, warn if the previous run
// terminated uncleanly, and manage the clean-shutdown marker. All wiring is defensive - a
// diagnostics failure here must never prevent the gateway from serving.
InstallCrashObservability(app);

app.Run();

void InstallCrashObservability(WebApplication application)
{
    try
    {
        var dataDirectory = BotNexusHome.ResolveDataPath() ?? BotNexusHome.ResolveHomePath();

        // 1. Last-chance fault handler: flush a structured [FTL] breadcrumb the instant the
        //    process is about to die (unhandled exception / unobserved task / abrupt exit), so
        //    even a dump-less hard exit leaves an investigable trail.
        var agentRegistry = application.Services.GetService<IAgentRegistry>();
        var probes = new BotNexus.Gateway.Diagnostics.FaultContextProbes(
            ActiveAgentCount: agentRegistry is null ? null : () => agentRegistry.GetAll().Count);
        new BotNexus.Gateway.Diagnostics.LastChanceFaultHandler(
            application.Logger,
            probes).Install();

        // 2. Detect how the previous run ended using the clean-shutdown marker, then clear it for
        //    this run so any subsequent hard exit is detectable as unclean on the next boot.
        var marker = new BotNexus.Gateway.Diagnostics.CleanShutdownMarker(
            new System.IO.Abstractions.FileSystem(),
            dataDirectory);
        var previousRun = marker.DetectPreviousRun();
        if (!previousRun.WasClean)
        {
            var lastKnown = previousRun.LastKnownUtc?.ToString("o") ?? "unknown";
            application.Logger.LogWarning(
                "previous gateway run terminated uncleanly (last-known clean-shutdown timestamp: {LastKnownTimestamp})",
                lastKnown);
        }
        marker.MarkRunning();

        // 3. On graceful shutdown, write the clean-shutdown marker so the next boot knows this run
        //    ended cleanly and does NOT emit the unclean-termination warning.
        var lifetime = application.Services.GetService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
        lifetime?.ApplicationStopped.Register(() =>
        {
            try { marker.MarkCleanShutdown(); }
            catch { /* best effort - a missed marker only risks a false unclean warning */ }
        });
    }
    catch (Exception ex)
    {
        // Crash observability is strictly additive - never let it break startup.
        application.Logger.LogWarning(ex, "Failed to install crash observability (continuing without it)");
    }
}

static void LogGatewayStartup(
    WebApplication app,
    IWebHostEnvironment environment,
    PlatformConfig startupPlatformConfig,
    WorldDescriptor worldDescriptor,
    string? configuredListenUrl)
{
    var worldIdentity = worldDescriptor.Identity;
    var gatewayAssembly = typeof(Program).Assembly;
    var version = gatewayAssembly.GetName().Version?.ToString() ?? "dev";
    var informationalVersion = gatewayAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? version;

    var agents = app.Services.GetRequiredService<IAgentRegistry>()
        .GetAll()
        .Select(agent => new
        {
            agentId = agent.AgentId.Value,
            agent.DisplayName,
            provider = agent.ApiProvider,
            model = agent.ModelId,
            isolation = agent.IsolationStrategy
        })
        .ToArray();

    var channels = app.Services.GetRequiredService<IChannelManager>()
        .Adapters
        .Select(channel => new
        {
            channelType = channel.ChannelType,
            channel.DisplayName,
            channel.SupportsStreaming
        })
        .ToArray();

    var loadedProviders = app.Services.GetRequiredService<ApiProviderRegistry>()
        .GetAll()
        .Select(provider => provider.Api)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    var configuredProviders = startupPlatformConfig.Providers?
        .Where(provider => provider.Value.Enabled)
        .Select(provider => provider.Key)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray() ?? [];

    var endpoints = app.Services.GetServices<EndpointDataSource>()
        .SelectMany(source => source.Endpoints.OfType<RouteEndpoint>())
        .Select(endpoint =>
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods?.ToArray() ?? ["*"];
            return new
            {
                route = endpoint.RoutePattern.RawText ?? endpoint.DisplayName ?? "<unknown>",
                methods,
                endpoint.DisplayName
            };
        })
        .OrderBy(endpoint => endpoint.route, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    var gatewayUrl = app.Urls.FirstOrDefault()
        ?? configuredListenUrl
        ?? app.Configuration["ASPNETCORE_URLS"]?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
        ?? "http://localhost:5000";
    var worldEmoji = string.IsNullOrWhiteSpace(worldIdentity.Emoji) ? "🌍" : worldIdentity.Emoji;

    app.Logger.LogWarning("{WorldEmoji} World: {WorldName} ({WorldId})", worldEmoji, worldIdentity.Name, worldIdentity.Id);
    app.Logger.LogWarning("📡 Gateway starting on {GatewayUrl}", gatewayUrl);

    app.Logger.LogInformation(
        "Gateway startup complete {GatewayVersion} ({InformationalVersion}) on {DotNetVersion}. env={Environment} providers={LoadedProviderCount} agents={AgentCount} channels={ChannelCount}",
        version,
        informationalVersion,
        Environment.Version,
        environment.EnvironmentName,
        loadedProviders.Length,
        agents.Length,
        channels.Length);

    app.Logger.LogInformation(
        "Gateway components loaded: configuredProviders={ConfiguredProviders} loadedProviders={LoadedProviders} agents={Agents} channels={Channels}",
        configuredProviders,
        loadedProviders,
        agents,
        channels);

    app.Logger.LogInformation("Gateway endpoints registered: {Endpoints}", endpoints);
    app.Logger.LogInformation(
        "World descriptor initialized: hostedAgents={HostedAgentCount} locations={LocationCount} strategies={StrategyCount} crossWorldPermissions={CrossWorldPermissionCount}",
        worldDescriptor.HostedAgents.Count,
        worldDescriptor.Locations.Count,
        worldDescriptor.AvailableStrategies.Count,
        worldDescriptor.CrossWorldPermissions.Count);
}

static string? GetGitCommitHash()
{
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", "rev-parse --short HEAD")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        var hash = proc?.StandardOutput.ReadToEnd().Trim();
        proc?.WaitForExit(3000);
        return string.IsNullOrEmpty(hash) ? null : hash;
    }
    catch { return null; }
}

// Returns true when the file exists and contains syntactically valid JSON.
// Used to keep a malformed config.json out of the IConfiguration pipeline so a parse
// failure can't escape on a ConfigurationManager reload thread and crash the host.
// A missing file is treated as "not valid" here; the caller handles the optional case.
static bool IsValidJsonFile(string path)
{
    if (!File.Exists(path))
        return false;

    try
    {
        using var stream = File.OpenRead(path);
        using var doc = System.Text.Json.JsonDocument.Parse(stream);
        return true;
    }
    catch (System.Text.Json.JsonException)
    {
        return false;
    }
    catch (IOException)
    {
        // Unreadable (e.g. locked) — treat as absent and run on defaults.
        return false;
    }
}

/// <summary>
/// Entry point marker for integration testing.
/// </summary>
public partial class Program;

/// <summary>No-op delegating handler used when provider HTTP logging is disabled.</summary>
file sealed class NoOpDelegatingHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => base.SendAsync(request, cancellationToken);
}
