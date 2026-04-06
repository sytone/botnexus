using BotNexus.Gateway.Api;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace BotNexus.Gateway.Tests;

public sealed class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ResponseAlwaysIncludesCorrelationHeader()
    {
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        context.Response.Headers.Should().ContainKey("X-Correlation-Id");
    }

    [Fact]
    public async Task InvokeAsync_UsesIncomingCorrelationId()
    {
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Correlation-Id"] = "incoming-id-123";

        await middleware.InvokeAsync(context);

        context.Response.Headers["X-Correlation-Id"].ToString().Should().Be("incoming-id-123");
        context.Items["CorrelationId"].Should().Be("incoming-id-123");
    }

    [Fact]
    public async Task InvokeAsync_UsesIncomingCorrelationId_InResponseAndContextItems()
    {
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Correlation-Id"] = "trace-abc";

        await middleware.InvokeAsync(context);

        context.Response.Headers["X-Correlation-Id"].ToString().Should().Be("trace-abc");
        context.Items["CorrelationId"].Should().Be("trace-abc");
    }

    [Fact]
    public async Task InvokeAsync_GeneratesCorrelationId_WhenMissing()
    {
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        context.Response.Headers["X-Correlation-Id"].ToString().Should().NotBeNullOrWhiteSpace();
        context.Items["CorrelationId"].Should().Be(context.Response.Headers["X-Correlation-Id"].ToString());
    }

    [Fact]
    public async Task InvokeAsync_GeneratedCorrelationId_IsValidGuid()
    {
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        Guid.TryParse(context.Response.Headers["X-Correlation-Id"].ToString(), out _).Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_WhenIncomingHeaderIsWhitespace_GeneratesNewGuid()
    {
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Correlation-Id"] = "   ";

        await middleware.InvokeAsync(context);

        var correlationId = context.Response.Headers["X-Correlation-Id"].ToString();
        Guid.TryParse(correlationId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_GeneratedCorrelationIds_AreUniquePerRequest()
    {
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        var firstContext = new DefaultHttpContext();
        var secondContext = new DefaultHttpContext();

        await middleware.InvokeAsync(firstContext);
        await middleware.InvokeAsync(secondContext);

        firstContext.Response.Headers["X-Correlation-Id"].ToString()
            .Should().NotBe(secondContext.Response.Headers["X-Correlation-Id"].ToString());
    }
}
