namespace BotNexus.Gateway.Contracts.Tools;

/// <summary>Closed provenance vocabulary classified by the origin of returned bytes, not tool power.</summary>
public static class ToolContentSource
{
    /// <summary>Bytes produced inside the agent's existing trust domain.</summary>
    public const string Local = "local";
    /// <summary>Bytes retrieved from a remote party.</summary>
    public const string Network = "network";
    /// <summary>Bytes from an integration outside trust assumptions.</summary>
    public const string Untrusted = "untrusted";
    /// <summary>Unclassified or malformed provenance; the fail-closed default.</summary>
    public const string Unknown = "unknown";

    /// <summary>Immutable vocabulary used by normalization and validation.</summary>
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Local, Network, Untrusted, Unknown });

    /// <summary>Normalizes case/whitespace; absent and unrecognized values become Unknown.</summary>
    public static string Normalize(this string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Unknown;
        var trimmed = value.Trim();
        foreach (var candidate in All)
            if (string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase)) return candidate;
        return Unknown;
    }

    /// <summary>Only Local avoids taint; unknown and any future sources fail closed.</summary>
    public static bool IsTainting(string? value) => Normalize(value) != Local;
}
