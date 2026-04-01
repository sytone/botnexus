using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Channels.Base;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Extensions;
using BotNexus.Core.Models;
using BotNexus.Gateway;
using BotNexus.Gateway.HealthChecks;
using BotNexus.Providers.Base;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.IncludeScopes = true);
builder.Services.AddBotNexus(builder.Configuration);

// Bind Kestrel to the configured gateway address
var gatewayCfg = builder.Configuration
    .GetSection($"{BotNexusConfig.SectionName}:Gateway")
    .Get<GatewayConfig>() ?? new GatewayConfig();

builder.WebHost.UseUrls($"http://{gatewayCfg.Host}:{gatewayCfg.Port}");

var app = builder.Build();

// JSON options for REST API responses
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter() }
};

// --- Static files for the Web UI ---
var webUiPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(webUiPath))
{
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = new PhysicalFileProvider(webUiPath)
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(webUiPath)
    });
}

var webSocketPath = string.IsNullOrWhiteSpace(gatewayCfg.WebSocketPath) ? "/ws" : gatewayCfg.WebSocketPath;
app.UseWhen(
    ctx =>
        ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
        ctx.Request.Path.StartsWithSegments(webSocketPath, StringComparison.OrdinalIgnoreCase),
    branch => branch.UseMiddleware<ApiKeyAuthenticationMiddleware>());

// --- Health ---
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = _ => true,
    ResponseWriter = HealthCheckJsonResponseWriter.WriteResponse,
    ResultStatusCodes =
    {
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy] = StatusCodes.Status200OK,
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status200OK,
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy] = StatusCodes.Status200OK
    }
});

app.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthCheckJsonResponseWriter.WriteResponse
});

// --- REST API: Sessions ---
app.MapGet("/api/sessions", async (ISessionManager sessionManager) =>
{
    var keys = await sessionManager.ListKeysAsync();
    var sessions = new List<object>();
    foreach (var key in keys)
    {
        var session = await sessionManager.GetOrCreateAsync(key, "unknown");
        sessions.Add(new
        {
            key = session.Key,
            agentName = session.AgentName,
            createdAt = session.CreatedAt,
            updatedAt = session.UpdatedAt,
            messageCount = session.History.Count,
            channel = session.Key.Contains(':') ? session.Key[..session.Key.IndexOf(':')] : "unknown"
        });
    }
    return Results.Json(sessions, jsonOptions);
});

app.MapGet("/api/sessions/{*key}", async (string key, ISessionManager sessionManager) =>
{
    var decoded = Uri.UnescapeDataString(key);
    var session = await sessionManager.GetOrCreateAsync(decoded, "unknown");
    if (session.History.Count == 0 && session.CreatedAt == session.UpdatedAt)
        return Results.NotFound(new { error = "Session not found" });

    return Results.Json(new
    {
        key = session.Key,
        agentName = session.AgentName,
        createdAt = session.CreatedAt,
        updatedAt = session.UpdatedAt,
        history = session.History.Select(e => new
        {
            role = e.Role.ToString().ToLowerInvariant(),
            content = e.Content,
            timestamp = e.Timestamp,
            toolName = e.ToolName,
            toolCallId = e.ToolCallId
        })
    }, jsonOptions);
});

// --- REST API: Channels ---
app.MapGet("/api/channels", (ChannelManager channelManager) =>
{
    var channels = channelManager.Channels.Select(c => new
    {
        name = c.Name,
        displayName = c.DisplayName,
        isRunning = c.IsRunning,
        supportsStreaming = c.SupportsStreaming
    });
    return Results.Json(channels, jsonOptions);
});

// --- REST API: Agents ---
app.MapGet("/api/agents", (IOptions<BotNexusConfig> config) =>
{
    var agentDefaults = config.Value.Agents;
    var agents = new List<object>
    {
        new
        {
            name = "default",
            model = agentDefaults.Model,
            maxTokens = agentDefaults.MaxTokens,
            temperature = agentDefaults.Temperature,
            maxToolIterations = agentDefaults.MaxToolIterations,
            timezone = agentDefaults.Timezone
        }
    };

    foreach (var (agentName, agentCfg) in agentDefaults.Named)
    {
        agents.Add(new
        {
            name = agentName,
            model = agentCfg.Model ?? agentDefaults.Model,
            maxTokens = agentCfg.MaxTokens ?? agentDefaults.MaxTokens,
            temperature = agentCfg.Temperature ?? agentDefaults.Temperature,
            maxToolIterations = agentCfg.MaxToolIterations ?? agentDefaults.MaxToolIterations,
            timezone = agentCfg.Timezone ?? agentDefaults.Timezone
        });
    }

    return Results.Json(agents, jsonOptions);
});

// --- REST API: Providers ---
app.MapGet("/api/providers", (ProviderRegistry providerRegistry) =>
{
    var names = providerRegistry.GetProviderNames();
    var providers = names.Select(name =>
    {
        var provider = providerRegistry.Get(name);
        return new
        {
            name,
            defaultModel = provider?.DefaultModel,
            model = provider?.Generation.Model,
            maxTokens = provider?.Generation.MaxTokens,
            temperature = provider?.Generation.Temperature
        };
    });
    return Results.Json(providers, jsonOptions);
});

// --- REST API: Tools ---
app.MapGet("/api/tools", (IEnumerable<ITool> tools) =>
{
    var toolList = tools.Select(t => new
    {
        name = t.Definition.Name,
        description = t.Definition.Description,
        parameterCount = t.Definition.Parameters.Count
    });
    return Results.Json(toolList, jsonOptions);
});

// --- REST API: Extensions summary ---
app.MapGet("/api/extensions", (
    ExtensionLoadReport report,
    ChannelManager channelManager,
    ProviderRegistry providerRegistry,
    IEnumerable<ITool> tools) =>
{
    return Results.Json(new
    {
        loaded = report.LoadedCount,
        failed = report.FailedCount,
        completed = report.Completed,
        healthy = report.CompletedSuccessfully,
        channels = channelManager.Channels.Count,
        providers = providerRegistry.GetProviderNames().Count,
        tools = tools.Count(),
        results = report.Results.Select(r => new
        {
            type = r.Type,
            key = r.Key,
            success = r.Success,
            message = r.Message
        })
    }, jsonOptions);
});

// --- WebSocket endpoint ---
if (gatewayCfg.WebSocketEnabled)
{
    app.Map(webSocketPath, static wsApp =>
    {
        wsApp.UseWebSockets();
        wsApp.Run(static async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("WebSocket upgrade required");
                return;
            }

            await ctx.RequestServices
                .GetRequiredService<GatewayWebSocketHandler>()
                .HandleAsync(ctx, ctx.RequestAborted);
        });
    });
}

foreach (var webhookHandler in app.Services.GetServices<IWebhookHandler>())
{
    var path = webhookHandler.Path.StartsWith("/", StringComparison.Ordinal)
        ? webhookHandler.Path
        : $"/{webhookHandler.Path}";

    app.MapPost(path, async (HttpContext context) =>
    {
        var result = await webhookHandler.HandleAsync(context).ConfigureAwait(false);
        await result.ExecuteAsync(context).ConfigureAwait(false);
    });
}

await app.RunAsync();

// Expose Program for WebApplicationFactory<Program> in integration tests
public partial class Program { }
