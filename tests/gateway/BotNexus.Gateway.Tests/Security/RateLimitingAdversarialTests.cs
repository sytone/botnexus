using System.Net;
using System.Reflection;
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

    // ── Entry-cap exhaustion (issue #1614) ───────────────────────────────────────
    // The per-client window dictionary must not grow without bound: an attacker who
    // churns distinct client keys faster than the lazy stale-eviction reclaims them
    // could otherwise drive the gateway to OOM. The fix caps the dictionary and, when
    // full, evicts only entries that are NOT actively rate-limiting a client.

    [Fact]
    public async Task InvokeAsync_FloodOfDistinctKeys_DictionarySizeNeverExceedsCap()
    {
        const int cap = 16;
        var middleware = new RateLimitingMiddleware(
            _ => Task.CompletedTask,
            CreateConfig(requestsPerMinute: 100, windowSeconds: 60, maxEntries: cap));

        // Mint far more distinct client keys than the cap allows, all within one window
        // so the once-per-60s idle cleanup cannot reclaim anything.
        for (var i = 1; i <= 500; i++)
        {
            var request = CreateContext($"10.{i / 256 % 256}.{i % 256}.7");
            await middleware.InvokeAsync(request);
            GetWindowCount(middleware).ShouldBeLessThanOrEqualTo(
                cap,
                $"dictionary exceeded the cap after {i} distinct keys");
        }

        GetWindowCount(middleware).ShouldBeLessThanOrEqualTo(cap);
    }

    [Fact]
    public async Task InvokeAsync_EntryCap_PreservesActivelyLimitedClientUnderFlood()
    {
        const int cap = 8;
        var middleware = new RateLimitingMiddleware(
            _ => Task.CompletedTask,
            CreateConfig(requestsPerMinute: 1, windowSeconds: 60, maxEntries: cap));

        // Drive a victim client into an active rate-limited state (over its limit).
        const string victimIp = "172.16.0.1";
        await middleware.InvokeAsync(CreateContext(victimIp));
        var blocked = CreateContext(victimIp);
        await middleware.InvokeAsync(blocked);
        blocked.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);

        // Now flood with many distinct keys, forcing repeated cap-eviction.
        for (var i = 1; i <= 300; i++)
            await middleware.InvokeAsync(CreateContext($"10.{i / 256 % 256}.{i % 256}.9"));

        // The actively-limited victim must still be throttled: a flood must not clear
        // an attacker's (or any active client's) own block by evicting its window.
        var stillBlocked = CreateContext(victimIp);
        await middleware.InvokeAsync(stillBlocked);
        stillBlocked.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
    }

    [Fact]
    public async Task InvokeAsync_EntryCapDisabled_DoesNotEvict()
    {
        // maxEntries <= 0 disables the cap (back-compat with the pre-#1614 behaviour):
        // every distinct key keeps its own window for the life of the window.
        var middleware = new RateLimitingMiddleware(
            _ => Task.CompletedTask,
            CreateConfig(requestsPerMinute: 100, windowSeconds: 60, maxEntries: 0));

        const int distinctKeys = 250;
        for (var i = 1; i <= distinctKeys; i++)
            await middleware.InvokeAsync(CreateContext($"10.{i / 256 % 256}.{i % 256}.11"));

        GetWindowCount(middleware).ShouldBe(distinctKeys);
    }

    [Fact]
    public void RateLimit_MaxEntries_DefaultsToTenThousand()
    {
        new RateLimitConfig().MaxEntries.ShouldBe(10_000);
    }

    [Fact]
    public void RateLimit_MaxEntries_IsReadFromGatewaySettings()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                RateLimit = new RateLimitConfig { MaxEntries = 250 }
            }
        };

        config.Gateway?.RateLimit?.MaxEntries.ShouldBe(250);
    }

    private static IOptions<PlatformConfig> CreateConfig(int requestsPerMinute, int windowSeconds, int? maxEntries = null)
        => Options.Create(new PlatformConfig()
        {
            Gateway = new GatewaySettingsConfig
            {
                RateLimit = new RateLimitConfig
                {
                    Enabled = true,
                    RequestsPerMinute = requestsPerMinute,
                    WindowSeconds = windowSeconds,
                    MaxEntries = maxEntries ?? new RateLimitConfig().MaxEntries
                }
            }
        });

    private static int GetWindowCount(RateLimitingMiddleware middleware)
    {
        var windows = typeof(RateLimitingMiddleware)
            .GetField("_clientWindows", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(middleware)!;
        return (int)windows.GetType()
            .GetProperty("Count", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(windows)!;
    }

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
