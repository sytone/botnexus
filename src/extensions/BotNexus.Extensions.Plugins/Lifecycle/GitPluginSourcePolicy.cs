using System.Text.RegularExpressions;

namespace BotNexus.Extensions.Plugins.Lifecycle;

// An allowlist, not a URL scrubber: reject ambiguous syntax before Git interprets it.
// Keep messages constant because neither sources nor transport diagnostics are trusted text.
internal static class GitPluginSourcePolicy
{
    internal const string RejectedSourceMessage = "Plugin source rejected. Use an HTTPS or SSH Git source without inline credentials, query parameters, or fragments. Local checkout handoff is not supported.";
    internal const string FetchFailureMessage = "Plugin source fetch failed. Check Git availability, connectivity, and repository access in the actual gateway or container environment, then retry. Git diagnostics are withheld because they may contain credentials.";

    private const string Host = @"[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?";
    private const string User = @"[A-Za-z0-9_][A-Za-z0-9_.-]*";
    private const string RepositoryPath = @"[A-Za-z0-9_~.-][A-Za-z0-9_~./-]*";

    internal static bool IsSupported(string? source)
    {
        if (string.IsNullOrEmpty(source) || source.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return false;
        }

        // No percent encoding, userinfo on HTTPS, password on SSH, query, fragment, backslash,
        // remote helpers, or local paths. SSH usernames (not passwords) remain supported.
        var urlPattern = $@"\A(?:https://{Host}(?::[0-9]+)?/{RepositoryPath}|ssh://(?:{User}@)?{Host}(?::[0-9]+)?/{RepositoryPath})\z";
        if (Regex.IsMatch(source, urlPattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
        {
            return Uri.TryCreate(source, UriKind.Absolute, out var uri)
                && !string.IsNullOrEmpty(uri.Host)
                && uri.HostNameType is UriHostNameType.Dns or UriHostNameType.IPv4;
        }

        // Require an explicit user@host for scp syntax so drive paths and helper names can never
        // be mistaken for remote sources. Port selection uses the ssh:// form instead.
        return Regex.IsMatch(source, $@"\A{User}@{Host}:/?{RepositoryPath}\z",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
}
