using System.Globalization;

namespace BotNexus.Agent.Providers.Copilot.Headers;

/// <summary>Bounded allowlist parser for URL-encoded quota snapshot headers. Never retains raw values.</summary>
public static class CopilotHeaderQuotaParser
{
    /// <summary>Upper bound on inspected header characters; larger snapshots are unknown.</summary>
    public const int MaxHeaderChars = 4096;
    private const int MaxPairs = 64;

    /// <summary>Parses only documented source fields, preserving percentages, counts, and the established unlimited sentinel pair separately.</summary>
    public static CopilotHeaderQuota Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > MaxHeaderChars) return CopilotHeaderQuota.Unknown;
        var pairs = raw.Split('&');
        if (pairs.Length > MaxPairs) return CopilotHeaderQuota.Unknown;
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) continue;
            var key = pair[..equals];
            if (key is not ("ent" or "rem" or "totRem" or "ov" or "ovPerm" or "rst")) continue;
            // Duplicate fields are ambiguous even when one happens to look valid.
            if (fields.ContainsKey(key)) { fields[key] = null; continue; }
            var encoded = pair[(equals + 1)..];
            fields[key] = HasValidEscapes(encoded) ? Uri.UnescapeDataString(encoded) : null;
        }
        string? Field(string key) => fields.GetValueOrDefault(key);
        var remaining = Number(Field("rem"));
        if (remaining > 100) remaining = null;
        var reset = Field("rst");
        // Require an explicit offset rather than silently using the host's timezone.
        DateTimeOffset? resetAt = reset is not null &&
            (reset.EndsWith('Z') || (reset.Length >= 6 && reset[^3] == ':' && reset[^6] is '+' or '-')) &&
            DateTimeOffset.TryParse(reset, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedReset)
            ? parsedReset : null;
        var entitlement = Number(Field("ent"));
        var totalRemaining = Number(Field("totRem"));
        // Only this established pair is unlimited. A lone sentinel or arbitrary negative is unknown.
        bool? unlimited = Field("ent") == "-1" && Field("totRem") == "-1" ? true
            : entitlement is not null && totalRemaining is not null ? false : null;
        return new(entitlement, remaining, totalRemaining, Number(Field("ov")),
            bool.TryParse(Field("ovPerm"), out var permitted) ? permitted : null, resetAt, unlimited);
    }

    private static decimal? Number(string? value) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= 0
            ? number : null;

    private static bool HasValidEscapes(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;
            if (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2])) return false;
            index += 2;
        }
        return true;
    }
}
