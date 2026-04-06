using BotNexus.Gateway.Api;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using System.Diagnostics;
using DiagnosticsActivity = System.Diagnostics.Activity;

namespace BotNexus.Gateway.Tests;

public sealed class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task CorrelationIdMiddleware_WhenActivityExists_UsesTraceIdAsCorrelationId()
    {
        var expectedTraceId = ActivityTraceId.CreateFromString("0123456789abcdef0123456789abcdef".AsSpan());
        var expectedSpanId = ActivitySpanId.CreateFromString("0123456789abcdef".AsSpan());
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        using var activity = new DiagnosticsActivity("gateway-request");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.SetParentId(expectedTraceId, expectedSpanId, ActivityTraceFlags.Recorded);
        activity.Start();

        await middleware.InvokeAsync(context);

        context.Response.Headers["X-Correlation-Id"].ToString().Should().Be(expectedTraceId.ToString());
    }

    [Fact]
    public async Task CorrelationIdMiddleware_WhenNoActivity_GeneratesCorrelationId()
    {
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        DiagnosticsActivity.Current = null;

        await middleware.InvokeAsync(context);

        context.Response.Headers["X-Correlation-Id"].ToString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CorrelationIdMiddleware_WhenClientSendsCorrelationId_PreservesIt()
    {
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Correlation-Id"] = "client-correlation-id";

        await middleware.InvokeAsync(context);

        context.Response.Headers["X-Correlation-Id"].ToString().Should().Be("client-correlation-id");
    }
}
