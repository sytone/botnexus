using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Cli.Commands;

namespace BotNexus.Cli.Tests;

public sealed class DebugGatewayCommandTests : IDisposable
{
    private readonly MockHttpServer _server;

    public DebugGatewayCommandTests()
    {
        _server = new MockHttpServer();
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Status_ReturnsZero_WhenGatewayHealthy()
    {
        _server.SetResponse("/health", HttpStatusCode.OK, "{}");
        _server.SetResponse("/api/diagnostics/threadpool", HttpStatusCode.OK,
            """{"pendingWorkItems":0,"workerThreads":8,"completionPortThreads":2,"healthy":true}""");
        _server.SetResponse("/api/diagnostics/activity", HttpStatusCode.OK,
            """{"lastActivityUtc":"2026-06-11T12:00:00Z","inactivitySeconds":5,"healthy":true}""");

        var result = await DebugGatewayCommand.ExecuteStatusAsync(_server.BaseUrl, "json", CancellationToken.None);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task Status_ReturnsOne_WhenGatewayUnhealthy()
    {
        _server.SetResponse("/health", HttpStatusCode.ServiceUnavailable, "");

        var result = await DebugGatewayCommand.ExecuteStatusAsync(_server.BaseUrl, "table", CancellationToken.None);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Status_ReturnsOne_WhenGatewayUnreachable()
    {
        var result = await DebugGatewayCommand.ExecuteStatusAsync("http://localhost:1", "table", CancellationToken.None);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Sessions_ReturnsZero_WithValidResponse()
    {
        _server.SetResponse("/api/sessions/stats", HttpStatusCode.OK,
            """{"totalSessions":10,"activeSessions":3,"agentBreakdown":{"farnsworth":5,"nova":5}}""");

        var result = await DebugGatewayCommand.ExecuteSessionsAsync(_server.BaseUrl, null, 20, "json", CancellationToken.None);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task Sessions_ReturnsOne_WhenUnreachable()
    {
        var result = await DebugGatewayCommand.ExecuteSessionsAsync("http://localhost:1", null, 20, "table", CancellationToken.None);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Providers_ReturnsZero_WithValidResponse()
    {
        _server.SetResponse("/api/providers", HttpStatusCode.OK,
            """[{"name":"copilot","providerId":"copilot","id":"copilot"}]""");
        _server.SetResponse("/api/models", HttpStatusCode.OK,
            """[{"id":"claude-sonnet-4-20250514","provider":"copilot"}]""");

        var result = await DebugGatewayCommand.ExecuteProvidersAsync(_server.BaseUrl, "json", CancellationToken.None);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task Providers_ReturnsOne_WhenUnreachable()
    {
        var result = await DebugGatewayCommand.ExecuteProvidersAsync("http://localhost:1", "table", CancellationToken.None);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Config_ReturnsZero_WithValidResponse()
    {
        _server.SetResponse("/api/config", HttpStatusCode.OK,
            """{"gateway":{"port":5005},"agents":{}}""");

        var result = await DebugGatewayCommand.ExecuteConfigAsync(_server.BaseUrl, null, "json", CancellationToken.None);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task Config_ReturnsOne_WhenSectionNotFound()
    {
        _server.SetResponse("/api/config", HttpStatusCode.OK,
            """{"gateway":{"port":5005}}""");

        var result = await DebugGatewayCommand.ExecuteConfigAsync(_server.BaseUrl, "nonexistent", "table", CancellationToken.None);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Config_FiltersBySection()
    {
        _server.SetResponse("/api/config", HttpStatusCode.OK,
            """{"gateway":{"port":5005},"agents":{"farnsworth":{}}}""");

        var result = await DebugGatewayCommand.ExecuteConfigAsync(_server.BaseUrl, "gateway", "json", CancellationToken.None);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task Config_ReturnsOne_WhenUnreachable()
    {
        var result = await DebugGatewayCommand.ExecuteConfigAsync("http://localhost:1", null, "json", CancellationToken.None);
        Assert.Equal(1, result);
    }
}

/// <summary>
/// Minimal HTTP server for testing gateway client commands without a real gateway.
/// </summary>
internal sealed class MockHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Dictionary<string, (HttpStatusCode status, string body)> _responses = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenTask;

    public string BaseUrl { get; }

    public MockHttpServer()
    {
        var port = FindFreePort();
        BaseUrl = $"http://localhost:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _listenTask = Task.Run(ListenLoop);
    }

    public void SetResponse(string path, HttpStatusCode status, string body)
    {
        _responses[path] = (status, body);
    }

    private async Task ListenLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                var path = context.Request.Url?.AbsolutePath ?? "/";

                // Strip query string for matching
                if (_responses.TryGetValue(path, out var response))
                {
                    context.Response.StatusCode = (int)response.status;
                    context.Response.ContentType = "application/json";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(response.body);
                    await context.Response.OutputStream.WriteAsync(bytes);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }
                context.Response.Close();
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
        }
    }

    private static int FindFreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        socket.Start();
        var port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        try { _listenTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}