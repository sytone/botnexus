using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using BotNexus.Satellite.Client.Core;

namespace BotNexus.Satellite.Client.SignalR;

/// <summary>Non-secret per-user tray profile; never used to assert gateway caller identity.</summary>
public sealed class ClientProfile
{
    public string GatewayEndpoint { get; init; } = "";
    public string SatelliteIdentifier { get; init; } = "";
    public bool ConnectOnLaunch { get; init; } = true;
    public bool AllowLoopbackHttp { get; init; }
    public bool UseTunnelAuthentication { get; init; }
    /// <summary>Heartbeat cadence in seconds, strictly below the matching gateway registration timeout.</summary>
    public int HeartbeatIntervalSeconds { get; init; } = 30;
    /// <summary>Copy the configured gateway satellite stale timeout; this preference does not change the server.</summary>
    public int StaleTimeoutSeconds { get; init; } = 120;

    [JsonIgnore]
    public string CredentialScope
    {
        get
        {
            Validate();
            var hub = ToOptions().GetHubUri();
            var origin = $"{hub.Scheme}://{hub.IdnHost.ToLowerInvariant()}:{hub.Port}";
            var bytes = Encoding.UTF8.GetBytes(origin + "\n" + SatelliteIdentifier.ToLowerInvariant());
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
    }

    public SatelliteClientOptions ToOptions() => new()
    {
        GatewayEndpoint = GatewayEndpoint,
        AllowLoopbackHttp = AllowLoopbackHttp,
        TunnelBrowserOrigin = UseTunnelAuthentication ? GatewayEndpoint : null,
        HeartbeatInterval = TimeSpan.FromSeconds(HeartbeatIntervalSeconds),
        StaleTimeout = TimeSpan.FromSeconds(StaleTimeoutSeconds)
    };

    public void Validate()
    {
        var errors = ToOptions().Validate().ToList();
        if (string.IsNullOrEmpty(SatelliteIdentifier) || SatelliteIdentifier.Length > 128 ||
            !SatelliteIdentifier.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            errors.Add("Satellite identifier must contain 1–128 letters, digits, dots, hyphens or underscores.");
        if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors));
    }
}
