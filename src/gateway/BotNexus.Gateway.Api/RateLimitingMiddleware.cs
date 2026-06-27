using System.Collections.Concurrent;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Api;

/// <summary>
/// Applies per-client request rate limiting to Gateway HTTP requests.
/// </summary>
public sealed class RateLimitingMiddleware
{
    private const int DefaultRequestsPerMinute = 300;
    private const int DefaultWindowSeconds = 60;
    private const int MaxEvictionAttempts = 8;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinimumRetryAfter = TimeSpan.FromSeconds(1);

    private readonly RequestDelegate _next;
    private readonly IOptions<PlatformConfig> _platformConfig;
    private readonly ConcurrentDictionary<string, ClientWindow> _clientWindows = new(StringComparer.Ordinal);
    private long _lastCleanupTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitingMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="platformConfig">Loaded platform configuration.</param>
    public RateLimitingMiddleware(RequestDelegate next, IOptions<PlatformConfig> platformConfig)
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
        // Rate limiting is opt-in. Disabled by default for local development.
        var configuredRateLimit = _platformConfig.Value.Gateway?.RateLimit;
        if (configuredRateLimit?.Enabled != true)
        {
            await _next(context);
            return;
        }

        if (IsExemptPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var requestsPerMinute = configuredRateLimit?.RequestsPerMinute > 0
            ? configuredRateLimit.RequestsPerMinute
            : DefaultRequestsPerMinute;
        var windowSeconds = configuredRateLimit?.WindowSeconds > 0
            ? configuredRateLimit.WindowSeconds
            : DefaultWindowSeconds;
        // A non-positive cap disables entry-count bounding (pre-#1614 behaviour).
        var maxEntries = configuredRateLimit?.MaxEntries > 0
            ? configuredRateLimit.MaxEntries
            : 0;

        var now = DateTimeOffset.UtcNow;
        var windowLength = TimeSpan.FromSeconds(windowSeconds);
        TryCleanupStaleEntries(now, windowLength);
        var clientKey = GetClientKey(context);
        var clientWindow = TryAcquireWindow(clientKey, now, windowLength, requestsPerMinute, maxEntries);
        if (clientWindow is null)
        {
            // The tracking dictionary is at capacity and no idle/non-limiting entry could be
            // freed for this new client key. Reject rather than grow without bound. Existing
            // clients (already tracked) are never affected by this path.
            WriteOverflowResponse(context, requestsPerMinute, windowLength, now);
            return;
        }

        bool isLimited;
        TimeSpan retryAfter;
        int remaining;
        long resetEpoch;
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
            remaining = Math.Max(0, requestsPerMinute - clientWindow.RequestCount);
            resetEpoch = (clientWindow.WindowStart + windowLength).ToUnixTimeSeconds();
            retryAfter = (clientWindow.WindowStart + windowLength) - now;
            if (retryAfter < MinimumRetryAfter)
                retryAfter = MinimumRetryAfter;
        }

        if (isLimited)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString();
            context.Response.Headers["X-RateLimit-Limit"] = requestsPerMinute.ToString();
            context.Response.Headers["X-RateLimit-Remaining"] = "0";
            context.Response.Headers["X-RateLimit-Reset"] = resetEpoch.ToString();
            return;
        }

        context.Response.Headers["X-RateLimit-Limit"] = requestsPerMinute.ToString();
        context.Response.Headers["X-RateLimit-Remaining"] = remaining.ToString();
        context.Response.Headers["X-RateLimit-Reset"] = resetEpoch.ToString();

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

    /// <summary>
    /// Resolves the tracking window for <paramref name="clientKey"/>, enforcing the configured
    /// entry cap for new keys. Existing keys always resolve. When the dictionary is at capacity
    /// and the key is new, this prunes stale entries and then evicts a single window that is not
    /// actively rate-limiting a client to make room. Returns <c>null</c> only when the cap is
    /// reached and no room can be freed without evicting an actively-limiting window -- in which
    /// case the caller rejects the request rather than growing the dictionary without bound.
    /// </summary>
    private ClientWindow? TryAcquireWindow(
        string clientKey,
        DateTimeOffset now,
        TimeSpan windowLength,
        int requestsPerMinute,
        int maxEntries)
    {
        // Existing clients always keep their window; the cap only gates new key insertion.
        if (_clientWindows.TryGetValue(clientKey, out var existing))
            return existing;

        // Cap disabled, or there is headroom: insert (or pick up a racing insert) directly.
        if (maxEntries <= 0 || _clientWindows.Count < maxEntries)
            return _clientWindows.GetOrAdd(clientKey, _ => new ClientWindow(now));

        // At capacity for a new key: try to reclaim a slot, then re-check headroom. Loop a
        // bounded number of times because a concurrent insert could re-fill the freed slot.
        for (var attempt = 0; attempt < MaxEvictionAttempts; attempt++)
        {
            if (_clientWindows.TryGetValue(clientKey, out var raced))
                return raced;
            if (_clientWindows.Count < maxEntries)
                return _clientWindows.GetOrAdd(clientKey, _ => new ClientWindow(now));
            if (!TryEvictNonLimitingEntry(now, windowLength, requestsPerMinute))
                return null; // every retained window is actively limiting -- refuse the new key.
        }

        return _clientWindows.Count < maxEntries
            ? _clientWindows.GetOrAdd(clientKey, _ => new ClientWindow(now))
            : null;
    }

    /// <summary>
    /// Evicts a single window that is NOT actively rate-limiting a client (its window has
    /// expired, or it is under the request limit). Windows actively counting toward a 429 are
    /// preserved so a flood of new keys cannot clear an attacker's own throttle. Returns
    /// <c>true</c> if an entry was removed.
    /// </summary>
    private bool TryEvictNonLimitingEntry(DateTimeOffset now, TimeSpan windowLength, int requestsPerMinute)
    {
        foreach (var pair in _clientWindows)
        {
            var window = pair.Value;
            bool activelyLimiting;
            lock (window.Sync)
            {
                var windowExpired = now - window.WindowStart >= windowLength;
                // "Actively limiting" = still inside its window AND at/over the threshold, so the
                // next request from this client would be (or already is being) 429'd.
                activelyLimiting = !windowExpired && window.RequestCount >= requestsPerMinute;
            }

            if (!activelyLimiting && _clientWindows.TryRemove(pair.Key, out _))
                return true;
        }

        return false;
    }

    private static void WriteOverflowResponse(
        HttpContext context,
        int requestsPerMinute,
        TimeSpan windowLength,
        DateTimeOffset now)
    {
        var retryAfter = windowLength < MinimumRetryAfter ? MinimumRetryAfter : windowLength;
        var resetEpoch = (now + windowLength).ToUnixTimeSeconds();
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString();
        context.Response.Headers["X-RateLimit-Limit"] = requestsPerMinute.ToString();
        context.Response.Headers["X-RateLimit-Remaining"] = "0";
        context.Response.Headers["X-RateLimit-Reset"] = resetEpoch.ToString();
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

    private static bool IsExemptPath(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value)) return false;
        return value.Equals("/health", StringComparison.OrdinalIgnoreCase)
            || IsStaticFileRequest(path);
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
