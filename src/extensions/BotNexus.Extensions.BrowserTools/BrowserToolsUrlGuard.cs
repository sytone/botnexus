using System.Text.RegularExpressions;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Extensions.BrowserTools;

/// <summary>
/// The single URL admission decision for every browser navigation and every snapshot (#3030).
/// </summary>
/// <remarks>
/// <para>
/// Three independent hazards are checked here, in this order:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>SSRF.</b> Delegated in full to the shared <see cref="SsrfValidator"/>. This class contains
/// no private-range, loopback or cloud-metadata arithmetic of its own, and an architecture fence
/// asserts that it never will. A second copy of that policy is how one copy drifts while the
/// other keeps passing its tests.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Secret material in the target.</b> A prompt-injected page can ask the agent to navigate to
/// an attacker host with the agent's own API key pasted into the path or query - a one-request
/// exfiltration channel that no network-level rule can see, because the destination is a
/// perfectly ordinary public host.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Credential-like parameter names.</b> The same exfiltration with an opaque value the prefix
/// rules cannot recognise. Rejecting on the NAME catches the shape rather than the vocabulary.
/// </description>
/// </item>
/// </list>
/// <para>
/// Checks 2 and 3 run against the raw target AND its percent-decoded form. Decoding is applied
/// repeatedly to a fixed point, because a single decode pass leaves <c>%2525</c>-style
/// double-encoding intact and a guard that only decodes once is trivially bypassed.
/// </para>
/// </remarks>
public static partial class BrowserToolsUrlGuard
{
    private const int MaxDecodePasses = 5;

    /// <summary>
    /// Decides whether a URL may be navigated to, or whether content from it may be returned.
    /// </summary>
    /// <param name="url">The candidate URL, as supplied by the model or read back from the page.</param>
    /// <param name="config">Guard configuration; <c>null</c> uses defaults.</param>
    /// <returns>An allowed result, or a denial carrying the reason.</returns>
    public static BrowserGuardResult Validate(string? url, BrowserToolsConfig? config = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BrowserGuardResult.Denied("Browser navigation denied: no URL was supplied.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return BrowserGuardResult.Denied(
                "Browser navigation denied: the URL is not an absolute http(s) URL.");
        }

        var ssrf = SsrfValidator.Validate(uri, config?.AdditionalBlockedHosts);
        if (!ssrf.IsSafe)
        {
            return BrowserGuardResult.Denied($"Browser navigation denied: {ssrf.Reason}");
        }

        foreach (var form in ExpandEncodings(url))
        {
            if (SecretPrefixPattern().IsMatch(form))
            {
                return BrowserGuardResult.Denied(
                    "Browser navigation denied: the URL contains what looks like an API key or "
                    + "access token. Navigating would disclose it to the destination host.");
            }
        }

        var credential = FindCredentialParameter(uri, url);
        if (credential is not null)
        {
            return BrowserGuardResult.Denied(
                $"Browser navigation denied: the URL carries a credential-like query parameter "
                + $"'{credential}'. Navigating would disclose its value to the destination host.");
        }

        return BrowserGuardResult.Allowed;
    }

    /// <summary>
    /// Yields the raw value plus each successive percent-decoding, stopping at a fixed point.
    /// </summary>
    internal static IEnumerable<string> ExpandEncodings(string value)
    {
        var current = value;
        yield return current;

        for (var i = 0; i < MaxDecodePasses; i++)
        {
            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(current);
            }
            catch (UriFormatException)
            {
                // Malformed escape sequence: nothing further can be learned by decoding, and the
                // raw form has already been inspected.
                yield break;
            }

            if (string.Equals(decoded, current, StringComparison.Ordinal))
            {
                yield break;
            }

            current = decoded;
            yield return current;
        }
    }

    private static string? FindCredentialParameter(Uri uri, string rawUrl)
    {
        // The query is read from BOTH the parsed URI and the raw string: a percent-encoded '?'
        // or '&' hides a parameter from Uri.Query while a browser may still act on it after its
        // own normalisation, so the raw decoded forms are scanned too.
        var candidates = new List<string> { uri.Query };
        foreach (var form in ExpandEncodings(rawUrl))
        {
            var markerIndex = form.IndexOf('?', StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                candidates.Add(form[markerIndex..]);
            }
        }

        foreach (var query in candidates)
        {
            if (string.IsNullOrEmpty(query))
            {
                continue;
            }

            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                var rawName = eq >= 0 ? pair[..eq] : pair;
                var hasValue = eq >= 0 && eq < pair.Length - 1;
                if (!hasValue)
                {
                    // A bare flag carries nothing to exfiltrate.
                    continue;
                }

                string name;
                try
                {
                    name = Uri.UnescapeDataString(rawName);
                }
                catch (UriFormatException)
                {
                    name = rawName;
                }

                if (CredentialParameterPattern().IsMatch(name))
                {
                    return name;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Shapes of well-known credential material. Deliberately prefix-anchored on issuer formats
    /// rather than entropy-scored: a false negative here is a missed exfiltration, but a false
    /// positive blocks an ordinary URL, and issuer prefixes give a bright line for both.
    /// </summary>
    [GeneratedRegex(
        @"(sk-ant-[A-Za-z0-9_\-]{16}|sk-[A-Za-z0-9]{20}|gh[pousr]_[A-Za-z0-9]{20}|github_pat_[A-Za-z0-9_]{20}|xox[baprs]-[A-Za-z0-9\-]{10}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{30}|eyJ[A-Za-z0-9_\-]{10}\.[A-Za-z0-9_\-]{10}\.)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretPrefixPattern();

    /// <summary>
    /// Query-parameter names whose value is credential material by convention. Matched as a whole
    /// name segment so an innocuous <c>tokenizer</c> or <c>keyboard</c> is not blocked.
    /// </summary>
    [GeneratedRegex(
        @"^(?:[A-Za-z0-9]+[_\-.])*(?:api[_\-.]?key|apikey|access[_\-.]?token|auth[_\-.]?token|id[_\-.]?token|refresh[_\-.]?token|bearer|session[_\-.]?token|client[_\-.]?secret|secret|password|passwd|pwd|credential|credentials|token|key|sig|signature)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialParameterPattern();
}
