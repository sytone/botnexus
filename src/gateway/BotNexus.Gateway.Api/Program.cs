using BotNexus.Gateway.Api.Extensions;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using BotNexus.Providers.Anthropic;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.OpenAI;
using BotNexus.Providers.OpenAICompat;
using Microsoft.OpenApi.Models;
using System.Reflection;

const string GatewayCorsPolicy = "GatewayCorsPolicy";

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});

var platformConfigPath = builder.Configuration["BotNexus:ConfigPath"];
var startupPlatformConfig = PlatformConfigLoader.Load(platformConfigPath, validateOnLoad: false);

builder.Services.AddBotNexusGateway();
builder.Services.AddPlatformConfiguration(platformConfigPath);
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
app.UseMiddleware<GatewayAuthMiddleware>();
app.UseSwagger();
app.UseSwaggerUI();
app.UseWebSockets();

app.MapControllers();
app.MapBotNexusGatewayWebSocket();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
