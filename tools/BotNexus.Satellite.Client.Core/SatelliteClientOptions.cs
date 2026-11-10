namespace BotNexus.Satellite.Client.Core;

/// <summary>Non-secret settings; credentials belong exclusively in the platform credential store.</summary>
public sealed class SatelliteClientOptions
{
    // Task.Delay's timer cannot represent longer finite delays.
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1L);

    /// <summary>Gateway origin, without user information, query strings or fragments.</summary>
    public required string GatewayEndpoint { get; init; }
    /// <summary>Explicit local-development exception; never permits HTTP to non-loopback hosts.</summary>
    public bool AllowLoopbackHttp { get; init; }
    /// <summary>Optional HTTPS devtunnels.ms origin matching the gateway for explicit browser sign-in.</summary>
    public string? TunnelBrowserOrigin { get; init; }
    /// <summary>Heartbeat cadence must be positive and shorter than the configured gateway stale timeout.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Gateway registration timeout supplied by the user, not inferred from the browser.</summary>
    public TimeSpan StaleTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Rejects unsafe configuration with secret-free diagnostic messages.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var gatewayValid = TryGetOrigin(GatewayEndpoint, out var gateway);
        if (!gatewayValid || gateway is null ||
            (gateway.Scheme != Uri.UriSchemeHttps &&
             !(gateway.Scheme == Uri.UriSchemeHttp && AllowLoopbackHttp && IsExactLoopback(GatewayEndpoint))))
        {
            errors.Add("Gateway endpoint must be an HTTPS origin or an explicitly enabled HTTP loopback origin.");
        }

        if (TunnelBrowserOrigin is not null &&
            (!TryGetOrigin(TunnelBrowserOrigin, out var tunnel) || tunnel is null ||
             tunnel.Scheme != Uri.UriSchemeHttps || !IsTunnelHost(tunnel.Host) ||
             !gatewayValid || gateway is null || !SameOrigin(gateway, tunnel)))
        {
            errors.Add("Tunnel browser origin must be an HTTPS devtunnels.ms origin matching the gateway.");
        }

        if (!IsFiniteDelay(HeartbeatInterval) || !IsFiniteDelay(StaleTimeout) || HeartbeatInterval >= StaleTimeout)
        {
            errors.Add("Heartbeat and stale durations must be positive finite timer durations, with heartbeat shorter than stale timeout.");
        }

        return errors;
    }

    /// <summary>Builds the dedicated identity-derived hub URI only after validating settings.</summary>
    public Uri GetHubUri()
    {
        var errors = Validate();
        if (errors.Count != 0)
        {
            throw new ArgumentException(string.Join(" ", errors));
        }

        return new Uri(new Uri(GatewayEndpoint, UriKind.Absolute), "/hub/satellite");
    }

    internal static bool IsFiniteDelay(TimeSpan duration) => duration > TimeSpan.Zero && duration <= MaximumDelay;

    private static bool TryGetOrigin(string? value, out Uri? origin)
    {
        origin = null;
        if (string.IsNullOrEmpty(value) || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            value.IndexOfAny(['\\', '@', '?', '#']) >= 0)
        {
            return false;
        }

        var authorityStart = value.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0)
        {
            return false;
        }

        // Inspect the original path too: URI normalization must not turn /path/.. into an allowed origin.
        var pathStart = value.IndexOf('/', authorityStart + 3);
        if (pathStart >= 0 && pathStart != value.Length - 1)
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out origin) &&
            (origin.Scheme == Uri.UriSchemeHttps || origin.Scheme == Uri.UriSchemeHttp) &&
            origin.Host.Length > 0 && origin.UserInfo.Length == 0 &&
            origin.AbsolutePath == "/";
    }

    private static bool IsExactLoopback(string endpoint)
    {
        var start = endpoint.IndexOf("://", StringComparison.Ordinal) + 3;
        var end = endpoint.IndexOf('/', start);
        var authority = end < 0 ? endpoint[start..] : endpoint[start..end];
        var hostEnd = authority.StartsWith('[') ? authority.IndexOf(']') + 1 : authority.IndexOf(':');
        var host = hostEnd < 0 ? authority : authority[..hostEnd];
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "[::1]";
    }

    private static bool IsTunnelHost(string host) => host.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase);

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;
}

/// <summary>Defines bounded exponential delays shared by initial connection and active reconnection.</summary>
public sealed class RetryPolicy
{
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maximumDelay;

    /// <summary>Specifies positive bounds; the initial delay must not exceed the maximum.</summary>
    public RetryPolicy(TimeSpan initialDelay, TimeSpan maximumDelay)
    {
        if (!SatelliteClientOptions.IsFiniteDelay(initialDelay) || !SatelliteClientOptions.IsFiniteDelay(maximumDelay) ||
            initialDelay > maximumDelay)
        {
            throw new ArgumentException("Retry bounds must be positive finite timer durations with initial no greater than maximum.");
        }

        _initialDelay = initialDelay;
        _maximumDelay = maximumDelay;
    }

    /// <summary>Returns a capped delay for a zero-based attempt, without overflow for large attempts.</summary>
    public TimeSpan GetDelay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        var ticks = _initialDelay.Ticks;
        // Saturate before multiplication; even int.MaxValue attempts take at most 56 iterations.
        for (var i = 0; i < attempt && ticks < _maximumDelay.Ticks; i++)
        {
            ticks = ticks > _maximumDelay.Ticks / 2 ? _maximumDelay.Ticks : ticks * 2;
        }

        return TimeSpan.FromTicks(ticks);
    }
}
