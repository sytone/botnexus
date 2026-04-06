using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

// TODO: This test suite will fully light up once GatewayAuthMiddleware is implemented.
public sealed class GatewayAuthMiddlewareTests
{
    [Fact(Skip = "Awaiting GatewayAuthMiddleware implementation.")]
    public Task AuthenticatedRequest_WithValidApiKey_ReturnsSuccess()
        => Task.CompletedTask;

    [Fact(Skip = "Awaiting GatewayAuthMiddleware implementation.")]
    public Task UnauthenticatedRequest_Returns401()
        => Task.CompletedTask;

    [Fact(Skip = "Awaiting GatewayAuthMiddleware implementation.")]
    public Task InvalidApiKey_Returns401()
        => Task.CompletedTask;

    [Fact(Skip = "Awaiting GatewayAuthMiddleware implementation.")]
    public Task HealthEndpoint_SkipsAuth()
        => Task.CompletedTask;

    [Fact(Skip = "Awaiting GatewayAuthMiddleware implementation.")]
    public Task SwaggerEndpoint_SkipsAuth()
        => Task.CompletedTask;

    [Fact(Skip = "Awaiting GatewayAuthMiddleware implementation.")]
    public Task WebUIEndpoint_SkipsAuth()
        => Task.CompletedTask;

    [Fact]
    public async Task DevModeBypass_WhenNoKeysConfigured_AllowsAll()
    {
        var handler = new ApiKeyGatewayAuthHandler(apiKey: null, NullLogger<ApiKeyGatewayAuthHandler>.Instance);
        var context = new GatewayAuthContext
        {
            Headers = new Dictionary<string, string>(),
            QueryParameters = new Dictionary<string, string>(),
            Path = "/api/messages",
            Method = "POST"
        };

        var result = await handler.AuthenticateAsync(context);

        result.IsAuthenticated.Should().BeTrue();
    }

    [Fact(Skip = "Awaiting GatewayAuthMiddleware implementation.")]
    public Task WebSocketEndpoint_RequiresAuth()
        => Task.CompletedTask;
}
