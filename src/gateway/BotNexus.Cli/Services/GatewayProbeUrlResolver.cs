using System.Text.Json;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Services;

/// <summary>
/// Resolves the base URL the CLI should connect to when it probes a gateway it just started.
/// </summary>
/// <remarks>
/// <para>
/// The CLI launches the gateway with <c>--urls http://localhost:{port}</c> and then health-checks
/// that same address. That reasoning is sound only while nothing overrides the bind, and something
/// routinely does: <c>Program.cs</c> ends with
/// </para>
/// <code>
/// var listenUrl = platformConfig.Gateway?.ListenUrl;
/// if (!string.IsNullOrWhiteSpace(listenUrl)) { app.Urls.Clear(); app.Urls.Add(listenUrl); }
/// </code>
/// <para>
/// so an operator who sets <c>gateway.listenUrl</c> to a LAN address gets a gateway that never
/// listens on loopback at all. The CLI then waits out its full readiness timeout and reports
/// "process alive but not healthy" for a gateway that started perfectly and is serving requests.
/// The failure is silent in the worst way: the exit code says the start failed, so scripts that
/// check it abort, while the gateway they just started keeps running.
/// </para>
/// <para>
/// This resolves the address the gateway will actually bind, using the same precedence the gateway
/// itself applies. It deliberately changes only where the CLI *looks*; the <c>--urls</c> argument is
/// left alone, because when a listen URL is configured that argument is already inert, and when one
/// is not, loopback is correct.
/// </para>
/// </remarks>
public static class GatewayProbeUrlResolver
{
    // Kestrel accepts these as "every interface". None of them is necessarily connectable as
    // written - "http://+:5005" is not even a legal Uri - so a wildcard bind is probed on loopback,
    // which is always part of what it bound.
    private static readonly string[] WildcardHosts = ["+", "*", "0.0.0.0", "[::]", "::"];

    /// <summary>
    /// Returns the base URL to probe, without a trailing slash.
    /// </summary>
    /// <param name="configuredListenUrl">The operator's <c>gateway.listenUrl</c>, if any.</param>
    /// <param name="fallbackPort">The port the CLI was asked to use, when no listen URL is set.</param>
    public static string Resolve(string? configuredListenUrl, int fallbackPort)
    {
        if (string.IsNullOrWhiteSpace(configuredListenUrl))
            return $"http://localhost:{fallbackPort}";

        // gateway.listenUrl is a single URL, but ASPNETCORE_URLS-style semicolon lists reach this
        // setting often enough to be worth tolerating. The first entry is the one the gateway adds.
        var first = configuredListenUrl
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(first))
            return $"http://localhost:{fallbackPort}";

        var (scheme, host, port) = SplitLoosely(first);
        if (host is null)
            return $"http://localhost:{fallbackPort}";

        if (WildcardHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            host = "localhost";

        return port is null
            ? $"{scheme}://{host}"
            : $"{scheme}://{host}:{port}";
    }

    /// <summary>
    /// Reads the configured listen URL from the default config location and resolves against it.
    /// </summary>
    /// <remarks>
    /// Read through <see cref="IPlatformConfigAccessor"/> rather than by loading the file: the
    /// effective listen URL can come from the SQLite store beside <c>config.json</c>, and a direct
    /// load would miss it and probe the wrong address for exactly the operators this fixes.
    /// Every failure falls back to loopback - this runs on the path that starts the gateway, and a
    /// config problem should surface as the gateway's own startup error, not as the CLI refusing
    /// to look for it.
    /// </remarks>
    /// <param name="fallbackPort">The port the CLI was asked to use.</param>
    /// <param name="accessor">Config source; defaults to the shared accessor.</param>
    /// <param name="configPath">Config file to read; defaults to the standard location.</param>
    public static string ResolveFromConfig(
        int fallbackPort,
        IPlatformConfigAccessor? accessor = null,
        string? configPath = null)
    {
        try
        {
            var config = (accessor ?? PlatformConfigAccessor.Shared)
                .Get(configPath ?? PlatformConfigLoader.DefaultConfigPath);
            return Resolve(config.Gateway?.ListenUrl, fallbackPort);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return $"http://localhost:{fallbackPort}";
        }
    }

    /// <summary>
    /// Splits a listen URL without going through <see cref="Uri"/>, which rejects the wildcard hosts
    /// Kestrel accepts. Returns a null host when the value is not shaped like a URL at all.
    /// </summary>
    private static (string Scheme, string? Host, string? Port) SplitLoosely(string value)
    {
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
            return ("http", null, null);

        var scheme = value[..schemeEnd];
        var rest = value[(schemeEnd + 3)..].TrimEnd('/');
        if (rest.Length == 0)
            return (scheme, null, null);

        // Drop any path, and keep an IPv6 literal's brackets intact while finding the port colon.
        var pathStart = rest.IndexOf('/', StringComparison.Ordinal);
        if (pathStart >= 0)
            rest = rest[..pathStart];

        var portStart = rest.StartsWith('[')
            ? rest.IndexOf(':', rest.IndexOf(']', StringComparison.Ordinal) + 1)
            : rest.LastIndexOf(':');

        return portStart < 0
            ? (scheme, rest, null)
            : (scheme, rest[..portStart], rest[(portStart + 1)..]);
    }
}
