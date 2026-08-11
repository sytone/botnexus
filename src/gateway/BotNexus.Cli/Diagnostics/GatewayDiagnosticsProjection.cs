using System.Text;
using System.Text.RegularExpressions;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Security;

namespace BotNexus.Cli.Diagnostics;

/// <summary>
/// The single projection every CLI diagnostic surface applies before printing a gateway URL
/// or a transport failure message (issue #2845).
///
/// <para>WHY THIS EXISTS: the gateway target is operator-supplied via <c>--url</c>, and an
/// operator may embed a credential in it - either as userinfo (<c>https://user:pass@host</c>)
/// or as a credential-shaped query parameter (<c>?token=...</c>). Every gateway command's
/// failure branch used to echo that URL verbatim next to <c>ex.Message</c>, and for an
/// <see cref="HttpRequestException"/> the message routinely contains the request URI as well.
/// The failure branch is exactly the branch whose output CI captures and retains, so this is
/// the one path that must not print either string raw.</para>
///
/// <para>THE RULE, stated once: <b>display</b> goes through this projection; the
/// <c>GatewayClientFactory</c> / probe path keeps the <b>unredacted</b> URL, because the
/// credential is what makes the request work. Redacting the transport input would be a
/// functional regression, not a security fix.</para>
///
/// <para>The message half delegates to <see cref="ISecretRedactor"/> rather than carrying a
/// second pattern list: one redaction vocabulary, not two.</para>
/// </summary>
internal static class GatewayDiagnosticsProjection
{
    /// <summary>Shown instead of an absent URL, so the format string never prints an empty slot.</summary>
    private const string AbsentUrl = "(none)";

    /// <summary>Shown instead of an absent failure message.</summary>
    private const string AbsentMessage = "(no details)";

    /// <summary>Replacement for anything credential-shaped.</summary>
    private const string Mask = "***";

    /// <summary>
    /// Query parameter names whose value is treated as a credential. Compared
    /// case-insensitively; matching is on the exact name, not a substring, so a benign
    /// <c>tokenCount</c> is not mangled while <c>Token</c> is.
    /// </summary>
    private static readonly string[] CredentialParameterNames =
    [
        "token",
        "access_token",
        "api_key",
        "apikey",
        "api-key",
        "key",
        "password",
        "secret",
    ];

    private static readonly ISecretRedactor Redactor = new SecretRedactor();

    /// <summary>
    /// Matches an absolute URI embedded in free text. Deliberately stops before quoting and
    /// bracketing characters so a URI at the end of a sentence - <c>"... (https://h/?token=x)."</c> -
    /// does not swallow the trailing punctuation into the host/query it is about to project.
    /// </summary>
    private static readonly Regex EmbeddedUriRegex = new(
        @"[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s<>""'`)\]}]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Matches a bare <c>user:password@</c> userinfo blob. Used as the fail-closed path for
    /// input that is not a parseable absolute URL: an unparseable value is exactly the case
    /// where we know least about the string, so it must still not print a password.
    /// </summary>
    private static readonly Regex BareUserInfoRegex = new(
        @"(?<![A-Za-z0-9+.\-])[^\s/:@]+:[^\s/:@]+@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Projects a gateway URL for display: userinfo is dropped entirely and credential-shaped
    /// query values are replaced with <c>***</c>. The parameter <i>name</i> is preserved so the
    /// operator can still see which credential form they supplied.
    /// </summary>
    public static string ProjectUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return AbsentUrl;

        var trimmed = url.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return BareUserInfoRegex.Replace(trimmed, Mask + "@");

        var builder = new StringBuilder();
        builder.Append(uri.Scheme).Append("://");
        builder.Append(uri.Authority);   // Authority excludes UserInfo by construction.
        builder.Append(uri.AbsolutePath);
        builder.Append(MaskQuery(uri.Query));

        if (!string.IsNullOrEmpty(uri.Fragment))
            builder.Append(uri.Fragment);

        return builder.ToString();
    }

    /// <summary>
    /// Projects a transport failure message for display: every absolute URI embedded in the
    /// message is passed through <see cref="ProjectUrl"/>, and the result is then run through
    /// the shared <see cref="ISecretRedactor"/> so the CLI inherits the same secret vocabulary
    /// as the session store, cron delivery, and provider logging.
    /// </summary>
    public static string ProjectMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return AbsentMessage;

        var withProjectedUris = EmbeddedUriRegex.Replace(message, match => ProjectUrl(match.Value));
        var withoutBareUserInfo = BareUserInfoRegex.Replace(withProjectedUris, Mask + "@");

        return Redactor.Redact(withoutBareUserInfo);
    }

    private static string MaskQuery(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?")
            return query;

        var body = query.StartsWith('?') ? query[1..] : query;
        var parts = body.Split('&');

        for (var i = 0; i < parts.Length; i++)
        {
            var separator = parts[i].IndexOf('=');
            if (separator <= 0)
                continue;

            var name = parts[i][..separator];
            if (IsCredentialParameter(name))
                parts[i] = name + "=" + Mask;
        }

        return "?" + string.Join('&', parts);
    }

    private static bool IsCredentialParameter(string name)
    {
        foreach (var candidate in CredentialParameterNames)
        {
            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
