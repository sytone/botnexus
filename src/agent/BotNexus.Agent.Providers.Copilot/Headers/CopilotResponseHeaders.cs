using System.Diagnostics;
using System.Globalization;
using System.Net.Http;

namespace BotNexus.Agent.Providers.Copilot.Headers;

/// <summary>
/// Reads Copilot-specific response headers off an <see cref="HttpResponseMessage"/>
/// and surfaces them as structured Activity tags. Designed to be called once
/// per HTTP request, immediately after <c>SendAsync</c> returns and before any
/// success-status check — so error responses still expose correlation IDs.
/// </summary>
/// <remarks>
/// Captures of real Copilot CLI traffic show several headers we previously
/// discarded:
/// <list type="bullet">
///   <item><c>x-copilot-service-request-id</c> — Copilot's own correlation GUID</item>
///   <item><c>x-request-id</c> — front-door request id</item>
///   <item><c>X-GitHub-Request-Id</c> — GitHub edge request id</item>
///   <item><c>x-copilot-api-exp-assignment-context</c> — experiment assignments</item>
///   <item><c>x-quota-snapshot-chat</c> / <c>-completions</c> / <c>-premium_interactions</c>
///         — URL-encoded per-feature quota snapshot</item>
/// </list>
/// All values are emitted as Activity tags under the <c>botnexus.copilot.*</c>
/// namespace. The quota snapshots are additionally parsed into individual
/// numeric tags for the six well-known fields
/// (<c>ent</c>, <c>ov</c>, <c>ovPerm</c>, <c>rem</c>, <c>rst</c>, <c>totRem</c>)
/// while the raw header value is preserved alongside so future fields are not
/// lost.
/// </remarks>
public static class CopilotResponseHeaders
{
    private const string TagPrefix = "botnexus.copilot.";

    private static readonly (string Header, string Tag)[] CorrelationHeaders =
    [
        ("x-copilot-service-request-id", TagPrefix + "service_request_id"),
        ("x-request-id", TagPrefix + "request_id"),
        ("X-GitHub-Request-Id", TagPrefix + "github_request_id"),
        ("x-copilot-api-exp-assignment-context", TagPrefix + "exp_assignment_context"),
    ];

    private static readonly (string Header, string Feature)[] QuotaHeaders =
    [
        ("x-quota-snapshot-chat", "chat"),
        ("x-quota-snapshot-completions", "completions"),
        ("x-quota-snapshot-premium_interactions", "premium_interactions"),
    ];

    /// <summary>
    /// Read the recognised Copilot response headers from <paramref name="response"/>
    /// and attach them as tags to <paramref name="activity"/>. Silently no-ops
    /// when either argument is null or the headers are missing.
    /// </summary>
    public static void EmitToActivity(HttpResponseMessage response, Activity? activity)
    {
        if (response is null || activity is null)
            return;

        foreach (var (header, tag) in CorrelationHeaders)
        {
            if (TryGetSingle(response, header, out var value))
                activity.SetTag(tag, value);
        }

        foreach (var (header, feature) in QuotaHeaders)
        {
            if (!TryGetSingle(response, header, out var raw))
                continue;

            // Preserve the raw value so any new fields Copilot adds remain visible.
            activity.SetTag(TagPrefix + "quota." + feature + ".raw", raw);

            foreach (var (key, parsedValue) in ParseQuotaSnapshot(raw))
            {
                activity.SetTag(TagPrefix + "quota." + feature + "." + key, parsedValue);
            }
        }
    }

    private static bool TryGetSingle(HttpResponseMessage response, string name, out string value)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            value = string.Join(",", values);
            return !string.IsNullOrEmpty(value);
        }

        if (response.Content?.Headers is not null
            && response.Content.Headers.TryGetValues(name, out var contentValues))
        {
            value = string.Join(",", contentValues);
            return !string.IsNullOrEmpty(value);
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Parse a URL-encoded quota snapshot value, e.g.
    /// <c>ent=-1&amp;ov=0.0&amp;ovPerm=false&amp;rem=100.0&amp;rst=2026-07-01T00:00:00Z&amp;totRem=-1</c>,
    /// into a sequence of (key, typed value) pairs. Numbers are returned as
    /// <see cref="double"/>, booleans as <see cref="bool"/>, everything else as
    /// the decoded <see cref="string"/>.
    /// </summary>
    internal static IEnumerable<KeyValuePair<string, object>> ParseQuotaSnapshot(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            yield break;

        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = pair.Substring(0, eq);
            var rawValue = Uri.UnescapeDataString(pair.Substring(eq + 1));
            yield return new KeyValuePair<string, object>(key, Coerce(rawValue));
        }
    }

    private static object Coerce(string value)
    {
        if (bool.TryParse(value, out var b))
            return b;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        return value;
    }
}
