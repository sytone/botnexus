using BotNexus.Gateway.Api.Extensions;
using BotNexus.Gateway.Api.Hubs;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using BotNexus.Providers.Anthropic;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.OpenAI;
using BotNexus.Providers.OpenAICompat;
using BotNexus.Cron;
using BotNexus.Cron.Extensions;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Trace;
using Serilog;
using System.Reflection;

const string GatewayCorsPolicy = "GatewayCorsPolicy";

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
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

        var configuredOrigins = startupPlatformConfig.GetCors()?.AllowedOrigins?
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
var listenUrl = platformConfig.GetListenUrl();
if (!string.IsNullOrWhiteSpace(listenUrl))
{
    app.Urls.Clear();
    app.Urls.Add(listenUrl);
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors(GatewayCorsPolicy);
app.UseSerilogRequestLogging();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<GatewayAuthMiddleware>();
app.UseMiddleware<RateLimitingMiddleware>();
app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();
app.MapHub<GatewayHub>("/hub/gateway");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/version", () =>
{
    var assembly = typeof(Program).Assembly;
    var buildTime = File.GetLastWriteTimeUtc(assembly.Location).ToString("yyyyMMddHHmmss");
    return Results.Ok(new { version = buildTime });
});
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>
/// Entry point marker for integration testing.
/// </summary>
public partial class Program;
