namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>Allowlist boundary for operational guard evidence, independent of display or control policy.</summary>
public static class GuardEvidenceSanitizer
{
    /// <summary>Maps a classifier category to the finite product vocabulary without retaining arbitrary text.</summary>
    public static string NormalizeKind(this string? kind) => kind switch
    {
        "edit-non-progress" or "unchanged-housekeeping" or "unchanged-status" or "unchanged-read"
            or "absolute-tool-result-limit" or "classified-non-progress" => kind,
        _ => "classified-non-progress"
    };

    /// <summary>Copies at most sixteen episodes and sixteen strict opaque GUID references per episode.</summary>
    public static IReadOnlyList<GuardObservation> Sanitize(IReadOnlyList<GuardObservation> observations)
        => observations.TakeLast(16).Select(g => g with
        {
            GuardKind = NormalizeKind(g.GuardKind),
            Disposition = g.Disposition is "warning" or "recovered" or "terminal" ? g.Disposition : "unknown",
            EvidenceReferences = g.EvidenceReferences
                .Where(r => r is { Length: 32 } && Guid.TryParseExact(r, "N", out _)
                    || r is { Length: 36 } && Guid.TryParseExact(r, "D", out _))
                .Distinct(StringComparer.Ordinal).Take(16).ToArray()
        }).ToArray();
}
