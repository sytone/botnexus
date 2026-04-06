using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class GatewayAuthMiddlewareTests
{
    [Fact]
    public async Task AuthenticatedRequest_WithValidApiKey_ReturnsSuccess()
    {
        var handler = new ApiKeyGatewayAuthHandler(apiKey: "test-key", NullLogger<ApiKeyGatewayAuthHandler>.Instance);
        var nextCalled = false;
        var middleware = new GatewayAuthMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            handler,
            NullLogger<GatewayAuthMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/agents";
        context.Request.Headers["X-Api-Key"] = "test-key";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task UnauthenticatedRequest_Returns401()
    {
        var handler = new ApiKeyGatewayAuthHandler(apiKey: "test-key", NullLogger<ApiKeyGatewayAuthHandler>.Instance);
        var middleware = new GatewayAuthMiddleware(
            _ => Task.CompletedTask,
            handler,
            NullLogger<GatewayAuthMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/agents";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task InvalidApiKey_Returns401()
    {
        var handler = new ApiKeyGatewayAuthHandler(apiKey: "test-key", NullLogger<ApiKeyGatewayAuthHandler>.Instance);
        var middleware = new GatewayAuthMiddleware(
            _ => Task.CompletedTask,
            handler,
            NullLogger<GatewayAuthMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/agents";
        context.Request.Headers["X-Api-Key"] = "wrong-key";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task HealthEndpoint_SkipsAuth()
    {
        var authHandler = new Mock<IGatewayAuthHandler>(MockBehavior.Strict);
        var nextCalled = false;
        var middleware = new GatewayAuthMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            authHandler.Object,
            NullLogger<GatewayAuthMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/health";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    [Theory]
    [InlineData("/swagger")]
    [InlineData("/swagger/v1/swagger.json")]
    public async Task SwaggerEndpoint_SkipsAuth(string path)
    {
        var authHandler = new Mock<IGatewayAuthHandler>(MockBehavior.Strict);
        var nextCalled = false;
        var middleware = new GatewayAuthMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            authHandler.Object,
            NullLogger<GatewayAuthMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task WebUIEndpoint_SkipsAuth()
    {
        var authHandler = new Mock<IGatewayAuthHandler>(MockBehavior.Strict);
        var nextCalled = false;
        var middleware = new GatewayAuthMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            authHandler.Object,
            NullLogger<GatewayAuthMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/webui/index.html";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

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

    [Fact]
    public async Task WebSocketEndpoint_RequiresAuth()
    {
        var handler = new ApiKeyGatewayAuthHandler(apiKey: "test-key", NullLogger<ApiKeyGatewayAuthHandler>.Instance);
        var middleware = new GatewayAuthMiddleware(
            _ => Task.CompletedTask,
            handler,
            NullLogger<GatewayAuthMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/ws";
        context.Features.Set<IHttpWebSocketFeature>(new StubWebSocketFeature { IsWebSocketRequest = true });
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task ApiPathWithExtension_DoesNotSkipAuth()
    {
        var handler = new ApiKeyGatewayAuthHandler(apiKey: "test-key", NullLogger<ApiKeyGatewayAuthHandler>.Instance);
        var nextCalled = false;
        var middleware = new GatewayAuthMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            handler,
            NullLogger<GatewayAuthMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/agents.json";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task StaticFileInWebRoot_SkipsAuth()
    {
        var authHandler = new Mock<IGatewayAuthHandler>(MockBehavior.Strict);
        var nextCalled = false;
        var middleware = new GatewayAuthMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            authHandler.Object,
            NullLogger<GatewayAuthMiddleware>.Instance);

        var fileProvider = new Mock<IFileProvider>(MockBehavior.Strict);
        var fileInfo = new Mock<IFileInfo>(MockBehavior.Strict);
        fileInfo.SetupGet(info => info.Exists).Returns(true);
        fileInfo.SetupGet(info => info.IsDirectory).Returns(false);
        fileProvider
            .Setup(provider => provider.GetFileInfo("app.js"))
            .Returns(fileInfo.Object);

        var webHostEnvironment = new Mock<IWebHostEnvironment>(MockBehavior.Strict);
        webHostEnvironment.SetupGet(environment => environment.WebRootFileProvider).Returns(fileProvider.Object);

        var services = new ServiceCollection()
            .AddSingleton<IWebHostEnvironment>(webHostEnvironment.Object)
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = services
        };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/app.js";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    private sealed class StubWebSocketFeature : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest { get; init; }

        public Task<System.Net.WebSockets.WebSocket> AcceptAsync(WebSocketAcceptContext context)
            => throw new NotSupportedException();
    }
}
