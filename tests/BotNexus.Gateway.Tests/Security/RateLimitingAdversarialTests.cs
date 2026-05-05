using System.Net;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Security;

[Trait("Category", "Security")]
public sealed class RateLimitingAdversarialTests
{
    [Fact]
    public async Task InvokeAsync_ManyUniqueIps_UseIndependentBuckets()
    {
        var middleware = new RateLimitingMiddleware(_ => Task.CompletedTask, CreateConfig(requestsPerMinute: 1, windowSeconds: 60));

        for (var i = 1; i <= 120; i++)
        {
            var firstRequest = CreateContext($"10.0.0.{i}");
            await middleware.InvokeAsync(firstRequest);
            firstRequest.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        }

        var repeatedIpRequest = CreateContext("10.0.0.1");
        await middleware.InvokeAsync(repeatedIpRequest);
        repeatedIpRequest.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);

        var unseenIpRequest = CreateContext("10.0.1.1");
        await middleware.InvokeAsync(unseenIpRequest);
        unseenIpRequest.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_XForwardedForSpoofing_DoesNotOverrideRemoteIpBucket()
    {
        var middleware = new RateLimitingMiddleware(_ => Task.CompletedTask, CreateConfig(requestsPerMinute: 1, windowSeconds: 60));

        var firstRequest = CreateContext("127.0.0.5", xForwardedFor: "203.0.113.1");
        await middleware.InvokeAsync(firstRequest);

        var secondRequest = CreateContext("127.0.0.5", xForwardedFor: "198.51.100.9");
        await middleware.InvokeAsync(secondRequest);

        firstRequest.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        secondRequest.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
    }

    [Fact]
    public async Task InvokeAsync_RapidBurstAtLimitBoundary_UsesCorrectAllowAndDeny()
    {
        var middleware = new RateLimitingMiddleware(_ => Task.CompletedTask, CreateConfig(requestsPerMinute: 3, windowSeconds: 60));

        var requests = Enumerable.Range(0, 4)
            .Select(_ => CreateContext("127.0.0.10"))
            .ToList();

        foreach (var request in requests)
            await middleware.InvokeAsync(request);

        requests.Take(3).ShouldAllBe(request => request.Response.StatusCode == StatusCodes.Status200OK);
        requests[3].Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
    }

    [Fact]
    public async Task InvokeAsync_AfterWindowExpires_AllowsRequestsAgain()
    {
        var middleware = new RateLimitingMiddleware(_ => Task.CompletedTask, CreateConfig(requestsPerMinute: 1, windowSeconds: 1));

        var firstRequest = CreateContext("127.0.0.20");
        await middleware.InvokeAsync(firstRequest);

        var secondRequest = CreateContext("127.0.0.20");
        await middleware.InvokeAsync(secondRequest);
        secondRequest.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);

        await Task.Delay(TimeSpan.FromMilliseconds(1100));

        var thirdRequest = CreateContext("127.0.0.20");
        await middleware.InvokeAsync(thirdRequest);

        thirdRequest.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    private static IOptions<PlatformConfig> CreateConfig(int requestsPerMinute, int windowSeconds)
        => Options.Create(new PlatformConfig()
        {
            Gateway = new GatewaySettingsConfig
            {
                RateLimit = new RateLimitConfig
                {
                    Enabled = true,
                    RequestsPerMinute = requestsPerMinute,
                    WindowSeconds = windowSeconds
                }
            }
        });

    private static DefaultHttpContext CreateContext(string remoteIpAddress, string? xForwardedFor = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/chat";
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIpAddress);
        if (!string.IsNullOrWhiteSpace(xForwardedFor))
            context.Request.Headers["X-Forwarded-For"] = xForwardedFor;

        return context;
    }
}
