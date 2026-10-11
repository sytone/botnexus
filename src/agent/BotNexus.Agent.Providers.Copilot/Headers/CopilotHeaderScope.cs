namespace BotNexus.Agent.Providers.Copilot.Headers;

/// <summary>Immutable, non-secret account attribution supplied by the host credential resolver.</summary>
public sealed record CopilotHeaderScope
{
    /// <summary>Host-owned execution metadata key; absence denotes a legacy unattributed call.</summary>
    public const string MetadataKey = "botnexus.copilot.header_scope";
    /// <summary>Normalized configured provider instance; never inferred from a default response.</summary>
    public string Instance { get; }
    /// <summary>Opaque, non-secret credential generation that isolates account rotation.</summary>
    public string Generation { get; }
    /// <summary>Creates bounded immutable attribution from the host's verified credential resolution.</summary>
    public CopilotHeaderScope(string instance, string generation)
    {
        if (string.IsNullOrWhiteSpace(instance) || instance.Length > 128 || instance.Any(char.IsControl))
            throw new ArgumentException("Invalid Copilot instance.", nameof(instance));
        if (string.IsNullOrWhiteSpace(generation) || generation.Length > 128 || generation.Any(char.IsControl))
            throw new ArgumentException("Invalid Copilot generation.", nameof(generation));
        Instance = NormalizeInstance(instance);
        Generation = generation;
    }
    internal static string NormalizeInstance(string instance)
    {
        var normalized = instance.Trim().ToLowerInvariant();
        return normalized == "copilot" ? "github-copilot" : normalized;
    }
}
