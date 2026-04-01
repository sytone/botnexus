using BotNexus.Core.Configuration;
using BotNexus.Gateway;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBotNexus(builder.Configuration);

// Bind Kestrel to the configured gateway address
var gatewayCfg = builder.Configuration
    .GetSection($"{BotNexusConfig.SectionName}:Gateway")
    .Get<GatewayConfig>() ?? new GatewayConfig();

builder.WebHost.UseUrls($"http://{gatewayCfg.Host}:{gatewayCfg.Port}");

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

if (gatewayCfg.WebSocketEnabled)
{
    app.Map(gatewayCfg.WebSocketPath, static wsApp =>
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

await app.RunAsync();

// Expose Program for WebApplicationFactory<Program> in integration tests
public partial class Program { }
