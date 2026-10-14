using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Configuration;

/// <summary>Internal-use local credential scope. Token is excluded from serialization and diagnostics.</summary>
public sealed class CopilotAccountCredential(string instance, string generation, string oauthToken)
{
    /// <summary>Canonical configured instance.</summary>
    public string Instance { get; } = instance;
    /// <summary>Process-keyed, non-secret generation fingerprint.</summary>
    public string Generation { get; } = generation;
    /// <summary>OAuth credential for internal discovery only; never serialize.</summary>
    [JsonIgnore]
    public string OAuthToken { get; } = oauthToken;
    /// <summary>Safe diagnostic description containing no credential.</summary>
    public override string ToString() => "Copilot account credential";
}
