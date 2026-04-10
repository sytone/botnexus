using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Security;

[Trait("Category", "Security")]
public sealed class GatewayAuthMiddlewareAdversarialTests
{
    [Fact]
    public async Task InvokeAsync_PathTraversalAttempt_DoesNotBypassAuthentication()
    {
        await AssertPathRequiresAuthenticationAsync("/api/../../../etc/passwd");
    }

    [Fact]
    public async Task InvokeAsync_UrlEncodedTraversal_DoesNotBypassAuthentication()
    {
        await AssertPathRequiresAuthenticationAsync("/api%2F..%2F..%2Fetc%2Fpasswd");
    }

    [Fact]
    public async Task InvokeAsync_VeryLongPath_DoesNotBypassAuthentication()
    {
        var longPath = "/api/" + new string('a', 100_000);

        await AssertPathRequiresAuthenticationAsync(longPath);
    }

    [Fact]
    public async Task InvokeAsync_DoubleEncodedPath_DoesNotBypassAuthentication()
    {
        await AssertPathRequiresAuthenticationAsync("/api%252Fswagger");
    }

    [Fact]
    public async Task InvokeAsync_PathContainingNullByte_DoesNotBypassAuthentication()
    {
        var authHandler = new Mock<IGatewayAuthHandler>();
        authHandler
            .Setup(handler => handler.AuthenticateAsync(It.IsAny<GatewayAuthContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GatewayAuthResult.Failure("Invalid API key."));

        var middleware = new GatewayAuthMiddleware(
            _ => Task.CompletedTask,
            authHandler.Object,
            CreateWebHostEnvironment(),
            NullLogger<GatewayAuthMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Features.Get<IHttpRequestFeature>()!.Path = "/api/\0attack";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        authHandler.Verify(handler => handler.AuthenticateAsync(It.IsAny<GatewayAuthContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async Task AssertPathRequiresAuthenticationAsync(string path)
    {
        var authHandler = new Mock<IGatewayAuthHandler>();
        authHandler
            .Setup(handler => handler.AuthenticateAsync(It.IsAny<GatewayAuthContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GatewayAuthResult.Failure("Missing API key."));

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
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        authHandler.Verify(handler => handler.AuthenticateAsync(It.IsAny<GatewayAuthContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static IWebHostEnvironment CreateWebHostEnvironment()
    {
        var webHostEnvironment = new Mock<IWebHostEnvironment>(MockBehavior.Strict);
        webHostEnvironment.SetupGet(environment => environment.WebRootFileProvider).Returns(new NullFileProvider());
        return webHostEnvironment.Object;
    }
}
