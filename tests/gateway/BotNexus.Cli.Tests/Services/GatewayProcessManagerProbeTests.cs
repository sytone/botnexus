using System.Net;
using BotNexus.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Cli.Tests.Services;

/// <summary>
/// Tests for the HTTP probe logic in GatewayProcessManager that distinguishes
/// auth failures from unreachable endpoints (#757).
/// </summary>
public sealed class GatewayProcessManagerProbeTests
{
    private static GatewayProcessManager CreateManagerWithHandler(DelegatingHandler handler) =>
        new(
            Substitute.For<IHealthChecker>(),
            NullLogger<GatewayProcessManager>.Instance,
            probeClient: new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) });

    [Fact]
    public async Task ProbeGatewayAsync_Returns_Healthy_On_200_Response()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK);
        var manager = CreateManagerWithHandler(handler);

        var result = await manager.ProbeGatewayAsync(GatewayProcessManager.DefaultHealthUrl, CancellationToken.None);

        Assert.Equal(GatewayProbeResult.Healthy, result);
    }

    [Fact]
    public async Task ProbeGatewayAsync_Returns_ReachableNoAuth_On_401_Response()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.Unauthorized);
        var manager = CreateManagerWithHandler(handler);

        var result = await manager.ProbeGatewayAsync(GatewayProcessManager.DefaultHealthUrl, CancellationToken.None);

        Assert.Equal(GatewayProbeResult.ReachableNoAuth, result);
    }

    [Fact]
    public async Task ProbeGatewayAsync_Returns_ReachableNoAuth_On_403_Response()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.Forbidden);
        var manager = CreateManagerWithHandler(handler);

        var result = await manager.ProbeGatewayAsync(GatewayProcessManager.DefaultHealthUrl, CancellationToken.None);

        Assert.Equal(GatewayProbeResult.ReachableNoAuth, result);
    }

    [Fact]
    public async Task ProbeGatewayAsync_Returns_Unreachable_On_HttpRequestException()
    {
        var handler = new ThrowingHttpHandler(new HttpRequestException("Connection refused"));
        var manager = CreateManagerWithHandler(handler);

        var result = await manager.ProbeGatewayAsync("http://localhost:9999/health", CancellationToken.None);

        Assert.Equal(GatewayProbeResult.Unreachable, result);
    }

    [Fact]
    public async Task ProbeGatewayAsync_Returns_Unreachable_On_Timeout()
    {
        var handler = new ThrowingHttpHandler(new TaskCanceledException("timeout"));
        var manager = CreateManagerWithHandler(handler);

        var result = await manager.ProbeGatewayAsync("http://localhost:9999/health", CancellationToken.None);

        Assert.Equal(GatewayProbeResult.Unreachable, result);
    }

    [Fact]
    public async Task ProbeGatewayAsync_Returns_Healthy_On_Non_Auth_Non_2xx_Response()
    {
        // Non-2xx responses that are NOT 401/403 (e.g. 503 Service Unavailable) are treated
        // as Healthy from the auth-distinction perspective -- gateway is reachable, just unhealthy.
        var handler = new FakeHttpHandler(HttpStatusCode.ServiceUnavailable);
        var manager = CreateManagerWithHandler(handler);

        var result = await manager.ProbeGatewayAsync(GatewayProcessManager.DefaultHealthUrl, CancellationToken.None);

        Assert.Equal(GatewayProbeResult.Healthy, result);
    }

    /// <summary>
    /// A simple delegating handler that returns a fixed HTTP status code.
    /// </summary>
    private sealed class FakeHttpHandler(HttpStatusCode statusCode) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }

    /// <summary>
    /// A delegating handler that throws a specified exception to simulate connection failures.
    /// </summary>
    private sealed class ThrowingHttpHandler(Exception ex) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(ex);
    }
}
