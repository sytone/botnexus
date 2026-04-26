using BotNexus.Gateway.Api.Extensions;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using BotNexus.Agent.Providers.Anthropic;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using Microsoft.Extensions.Options;
using BotNexus.Agent.Providers.OpenAI;
using BotNexus.Agent.Providers.OpenAICompat;
using BotNexus.Cron;
using BotNexus.Cron.Extensions;
using BotNexus.Domain.World;
using Microsoft.AspNetCore.Routing;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Trace;
using Serilog;
using System.Reflection;

const string GatewayCorsPolicy = "GatewayCorsPolicy";

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(BotNexusHome.ResolveHomePath(), "logs", "botnexus-bootstrap-.log"),
        rollingInterval: RollingInterval.Day)
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args
});

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}",
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Warning)
    .WriteTo.File(
        Path.Combine(BotNexusHome.ResolveHomePath(), "logs", "botnexus-.log"),
        rollingInterval: RollingInterval.Hour,
        retainedFileCountLimit: 168),
    preserveStaticLogger: true);

var platformConfigPath = builder.Configuration["BotNexus:ConfigPath"];
var startupPlatformConfig = PlatformConfigLoader.Load(platformConfigPath, validateOnLoad: false);

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

builder.Services.AddBotNexusGateway(builder.Configuration);
builder.Services.AddBotNexusCron();
builder.Services.AddPlatformConfiguration(platformConfigPath);
builder.Services.Configure<CronOptions>(options =>
{
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
                WebhookUrl = pair.Value.WebhookUrl,
                ShellCommand = pair.Value.ShellCommand,
                Enabled = pair.Value.Enabled,
                CreatedBy = pair.Value.CreatedBy,
                Metadata = pair.Value.Metadata?.ToDictionary(entry => entry.Key, entry => (object?)entry.Value)
            },
            StringComparer.OrdinalIgnoreCase);
});
builder.Services.AddExtensionLoading();
builder.Services.AddSignalR();
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
builder.Services.AddHttpClient("BotNexus", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
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
    apiProviders.Register(new OpenAICompletionsProvider(httpClient, loggerFactory.CreateLogger<OpenAICompletionsProvider>()));
    apiProviders.Register(new OpenAIResponsesProvider(httpClient, loggerFactory.CreateLogger<OpenAIResponsesProvider>()));
    apiProviders.Register(new OpenAICompatProvider(httpClient));

    serviceProvider.GetRequiredService<BuiltInModels>().RegisterAll(models);

    // Register models from openai-compat providers in config (e.g. Ollama, LM Studio)
    var platformConfig = serviceProvider.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue;
    if (platformConfig.Providers is not null)
    {
        foreach (var (providerName, providerConfig) in platformConfig.Providers)
        {
            if (!providerConfig.Enabled || string.IsNullOrWhiteSpace(providerConfig.BaseUrl))
                continue;

            // Register each explicitly listed model for this provider
            if (providerConfig.Models is { Count: > 0 })
            {
                foreach (var modelId in providerConfig.Models)
                {
                    models.Register(providerName, new LlmModel(
                        Id: modelId,
                        Name: modelId,
                        Api: "openai-completions",
                        Provider: providerName,
                        BaseUrl: providerConfig.BaseUrl,
                        Reasoning: false,
                        Input: ["text"],
                        Cost: new ModelCost(0, 0, 0, 0),
                        ContextWindow: 128000,
                        MaxTokens: 32000));
                }
            }
        }
    }

    // Also register the default model for any agent using an openai-compat provider not in BuiltInModels
    if (platformConfig.Agents is not null && platformConfig.Providers is not null)
    {
        foreach (KeyValuePair<string, AgentDefinitionConfig> agentEntry in platformConfig.Agents)
        {
            var agentConfig = agentEntry.Value;
            if (string.IsNullOrWhiteSpace(agentConfig.Provider) || string.IsNullOrWhiteSpace(agentConfig.Model))
                continue;
            if (!platformConfig.Providers.TryGetValue(agentConfig.Provider, out var agentProvider))
                continue;
            if (string.IsNullOrWhiteSpace(agentProvider.BaseUrl))
                continue;
            if (models.GetModel(agentConfig.Provider, agentConfig.Model) is not null)
                continue; // already registered

            models.Register(agentConfig.Provider, new LlmModel(
                Id: agentConfig.Model,
                Name: agentConfig.Model,
                Api: "openai-completions",
                Provider: agentConfig.Provider,
                BaseUrl: agentProvider.BaseUrl,
                Reasoning: agentConfig.Model.Contains("reasoning", StringComparison.OrdinalIgnoreCase),
                Input: ["text"],
                Cost: new ModelCost(0, 0, 0, 0),
                ContextWindow: 128000,
                MaxTokens: 32000));
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

var app = builder.Build();

var platformConfig = app.Services.GetRequiredService<PlatformConfig>();
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
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
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

app.Run();

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

/// <summary>
/// Entry point marker for integration testing.
/// </summary>
public partial class Program;
