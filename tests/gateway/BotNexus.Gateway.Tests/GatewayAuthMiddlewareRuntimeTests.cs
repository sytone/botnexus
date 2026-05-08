using System.Text;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class GatewayAuthMiddlewareRuntimeTests
{
    [Fact]
    public async Task InvokeAsync_HealthPath_SkipsAuthentication()
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
            CreateWebHostEnvironment(),
            NullLogger<GatewayAuthMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/health";

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task InvokeAsync_AuthenticationFailure_Returns401Json()
    {
        var authHandler = new Mock<IGatewayAuthHandler>();
        authHandler.Setup(h => h.AuthenticateAsync(It.IsAny<GatewayAuthContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GatewayAuthResult.Failure("Missing API key."));

        var middleware = new GatewayAuthMiddleware(
            _ => Task.CompletedTask,
            authHandler.Object,
            CreateWebHostEnvironment(),
            NullLogger<GatewayAuthMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/agents";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        context.Response.Body.Position = 0;
        var payload = await new StreamReader(context.Response.Body).ReadToEndAsync();
        payload.ShouldContain("\"error\":\"unauthenticated\"");
    }

    [Fact]
    public async Task InvokeAsync_AgentNotAllowed_Returns403()
    {
        var authHandler = new Mock<IGatewayAuthHandler>();
        authHandler.Setup(h => h.AuthenticateAsync(It.IsAny<GatewayAuthContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                GatewayAuthResult.Success(new GatewayCallerIdentity
                {
                    CallerId = "caller-1",
                    AllowedAgents = ["agent-b"],
                    Permissions = []
                }));

        var middleware = new GatewayAuthMiddleware(
            _ => Task.CompletedTask,
            authHandler.Object,
            CreateWebHostEnvironment(),
            NullLogger<GatewayAuthMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/chat";
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"agentId":"agent-a","message":"hi"}"""));
        context.Request.ContentLength = context.Request.Body.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    private static IWebHostEnvironment CreateWebHostEnvironment()
    {
        var webHostEnvironment = new Mock<IWebHostEnvironment>(MockBehavior.Strict);
        webHostEnvironment.SetupGet(environment => environment.WebRootFileProvider).Returns(new NullFileProvider());
        return webHostEnvironment.Object;
    }
}

