using System.Collections.Concurrent;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Http;

namespace BotNexus.Gateway.Api;

/// <summary>
/// Applies per-client request rate limiting to Gateway HTTP requests.
/// </summary>
public sealed class RateLimitingMiddleware
{
    private const int DefaultRequestsPerMinute = 300;
    private const int DefaultWindowSeconds = 60;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinimumRetryAfter = TimeSpan.FromSeconds(1);

    private readonly RequestDelegate _next;
    private readonly PlatformConfig _platformConfig;
    private readonly ConcurrentDictionary<string, ClientWindow> _clientWindows = new(StringComparer.Ordinal);
    private long _lastCleanupTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitingMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="platformConfig">Loaded platform configuration.</param>
    public RateLimitingMiddleware(RequestDelegate next, PlatformConfig platformConfig)
    {
        _next = next;
        _platformConfig = platformConfig;
    }

    /// <summary>
    /// Processes the current request and enforces the configured per-client request limits.
    /// </summary>
    /// <param name="context">The HTTP request context.</param>
    /// <returns>A task representing middleware completion.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        // Exempt health checks, WebSocket upgrades, static files, and high-frequency polling endpoints
        var path = context.Request.Path;
        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
            context.WebSockets.IsWebSocketRequest ||
            path.StartsWithSegments("/hub", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/version", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/channels", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/log", StringComparison.OrdinalIgnoreCase) ||
            IsStaticFileRequest(path))
        {
            await _next(context);
            return;
        }

        var configuredRateLimit = _platformConfig.Gateway?.RateLimit;
        var requestsPerMinute = configuredRateLimit?.RequestsPerMinute > 0
            ? configuredRateLimit.RequestsPerMinute
            : DefaultRequestsPerMinute;
        var windowSeconds = configuredRateLimit?.WindowSeconds > 0
            ? configuredRateLimit.WindowSeconds
            : DefaultWindowSeconds;

        var now = DateTimeOffset.UtcNow;
        var windowLength = TimeSpan.FromSeconds(windowSeconds);
        TryCleanupStaleEntries(now, windowLength);
        var clientKey = GetClientKey(context);
        var clientWindow = _clientWindows.GetOrAdd(clientKey, _ => new ClientWindow(now));

        bool isLimited;
        TimeSpan retryAfter;
        lock (clientWindow.Sync)
        {
            if (now - clientWindow.WindowStart >= windowLength)
            {
                clientWindow.WindowStart = now;
                clientWindow.RequestCount = 0;
            }

            clientWindow.RequestCount++;
            clientWindow.LastAccessed = now;
            isLimited = clientWindow.RequestCount > requestsPerMinute;
            retryAfter = (clientWindow.WindowStart + windowLength) - now;
            if (retryAfter < MinimumRetryAfter)
                retryAfter = MinimumRetryAfter;
        }

        if (isLimited)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString();
            return;
        }

        await _next(context);
    }

    private void TryCleanupStaleEntries(DateTimeOffset now, TimeSpan windowLength)
    {
        var lastCleanupTicks = Interlocked.Read(ref _lastCleanupTicks);
        if (lastCleanupTicks > 0 && now - new DateTimeOffset(lastCleanupTicks, TimeSpan.Zero) < CleanupInterval)
            return;

        var nowTicks = now.UtcTicks;
        if (Interlocked.CompareExchange(ref _lastCleanupTicks, nowTicks, lastCleanupTicks) != lastCleanupTicks)
            return;

        var staleThreshold = windowLength + windowLength;
        foreach (var pair in _clientWindows)
        {
            var clientWindow = pair.Value;
            var lastAccessed = clientWindow.LastAccessed;
            if (now - lastAccessed > staleThreshold)
                _clientWindows.TryRemove(pair.Key, out _);
        }
    }

    private static string GetClientKey(HttpContext context)
    {
        if (context.Items.TryGetValue(GatewayAuthMiddleware.CallerIdentityItemKey, out var identityValue) &&
            identityValue is GatewayCallerIdentity callerIdentity &&
            !string.IsNullOrWhiteSpace(callerIdentity.CallerId))
        {
            return $"caller:{callerIdentity.CallerId}";
        }

        var ipAddress = context.Connection.RemoteIpAddress?.ToString();
        return string.IsNullOrWhiteSpace(ipAddress) ? "ip:unknown" : $"ip:{ipAddress}";
    }

    private sealed class ClientWindow(DateTimeOffset windowStart)
    {
        /// <summary>
        /// Gets the sync.
        /// </summary>
        public object Sync { get; } = new();
        /// <summary>
        /// Gets or sets the window start.
        /// </summary>
        public DateTimeOffset WindowStart { get; set; } = windowStart;
        /// <summary>
        /// Gets or sets the last accessed.
        /// </summary>
        public DateTimeOffset LastAccessed { get; set; } = windowStart;
        /// <summary>
        /// Gets or sets the request count.
        /// </summary>
        public int RequestCount { get; set; }
    }

    private static bool IsStaticFileRequest(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value)) return false;
        return value.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".map", StringComparison.OrdinalIgnoreCase);
    }
}
