using System.Text.Json;

namespace BotNexus.Integration.Tests;

/// <summary>
/// Minimal MCP server that implements the Streamable HTTP protocol.
/// Responds to 'initialize' and exposes a single 'ping' tool that returns 'pong'.
/// Used for integration testing MCP tool injection into agents.
/// </summary>
public sealed class McpPingServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly int _port;

    public McpPingServer(int port)
    {
        _port = port;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        _app = builder.Build();

        _app.MapPost("/", async (HttpContext ctx) =>
        {
            var body = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var method = body.RootElement.GetProperty("method").GetString();
            var id = body.RootElement.TryGetProperty("id", out var idEl) ? idEl : default;

            ctx.Response.ContentType = "application/json";

            var response = method switch
            {
                "initialize" => new
                {
                    jsonrpc = "2.0",
                    id = id.ValueKind == JsonValueKind.Number ? id.GetInt32() : 0,
                    result = new
                    {
                        protocolVersion = "2025-03-26",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "ping-server", version = "1.0.0" }
                    }
                } as object,

                "notifications/initialized" => null, // notification, no response needed

                "tools/list" => new
                {
                    jsonrpc = "2.0",
                    id = id.ValueKind == JsonValueKind.Number ? id.GetInt32() : 0,
                    result = new
                    {
                        tools = new[]
                        {
                            new
                            {
                                name = "ping",
                                description = "Returns pong. Use this to verify MCP connectivity.",
                                inputSchema = new
                                {
                                    type = "object",
                                    properties = new { },
                                    required = Array.Empty<string>()
                                }
                            }
                        }
                    }
                } as object,

                "tools/call" => new
                {
                    jsonrpc = "2.0",
                    id = id.ValueKind == JsonValueKind.Number ? id.GetInt32() : 0,
                    result = new
                    {
                        content = new[]
                        {
                            new { type = "text", text = "pong" }
                        }
                    }
                } as object,

                "ping" => new
                {
                    jsonrpc = "2.0",
                    id = id.ValueKind == JsonValueKind.Number ? id.GetInt32() : 0,
                    result = new { }
                } as object,

                _ => new
                {
                    jsonrpc = "2.0",
                    id = id.ValueKind == JsonValueKind.Number ? id.GetInt32() : 0,
                    error = new { code = -32601, message = $"Method not found: {method}" }
                } as object
            };

            if (response is not null)
            {
                await ctx.Response.WriteAsJsonAsync(response, ctx.RequestAborted);
            }
            else
            {
                ctx.Response.StatusCode = 204;
            }
        });

        // Reject GET (Streamable HTTP is POST-only)
        _app.MapGet("/", () => Results.StatusCode(405));
    }

    public int Port => _port;
    public string Url => $"http://127.0.0.1:{_port}";

    public Task StartAsync(CancellationToken ct = default) => _app.StartAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
    }
}
