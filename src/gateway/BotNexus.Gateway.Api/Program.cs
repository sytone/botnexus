using BotNexus.Gateway.Api.Extensions;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using BotNexus.Providers.Anthropic;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.OpenAI;
using BotNexus.Providers.OpenAICompat;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});

builder.Services.AddBotNexusGateway();
builder.Services.AddPlatformConfiguration(builder.Configuration["BotNexus:ConfigPath"]);
builder.Services.AddBotNexusGatewayApi();
builder.Services.AddSingleton<ApiProviderRegistry>();
builder.Services.AddSingleton<ModelRegistry>();
builder.Services.AddSingleton<BuiltInModels>();
builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
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
app.UseMiddleware<GatewayAuthMiddleware>();
app.UseWebSockets();

app.MapControllers();
app.MapBotNexusGatewayWebSocket();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
