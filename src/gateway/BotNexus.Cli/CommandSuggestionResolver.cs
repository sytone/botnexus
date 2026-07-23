using System.CommandLine;

namespace BotNexus.Cli;

/// <summary>
/// Produces actionable "did you mean" guidance for command tokens that fail to
/// match at the position the user typed them.
/// <para>
/// The BotNexus CLI is noun-first (<c>botnexus agent list</c>) but natural language
/// is verb-first (<c>list agents</c>). When a user inverts the order, System.CommandLine's
/// default typo engine suggests the bare unmatched token (e.g. "did you mean 'list'?" in
/// response to the user typing <c>list</c>) because a subcommand literally named <c>list</c>
/// exists under several parents. That self-referential suggestion is worse than useless.
/// </para>
/// <para>
/// This resolver instead walks the command tree and offers the fully-qualified parent-scoped
/// forms (<c>agent list</c>, <c>cron list</c>, <c>conversation list</c>). It never emits a
/// suggestion byte-identical to the token the user typed.
/// </para>
/// </summary>
internal static class CommandSuggestionResolver
{
    /// <summary>
    /// Builds fully-qualified, parent-scoped suggestions for an unmatched root token.
    /// <para>
    /// Walks every command reachable from <paramref name="root"/> and returns the space-joined
    /// path (relative to the root, so the executable name is excluded) of any command whose own
    /// name or alias matches <paramref name="unmatchedToken"/> case-insensitively and that lives
    /// under at least one parent. A top-level command whose name equals the token — which would
    /// reproduce the useless self-referential suggestion — is deliberately excluded, satisfying
    /// the invariant that a suggestion identical to the input is never shown.
    /// </para>
    /// </summary>
    /// <param name="root">The configured root command whose subtree is searched.</param>
    /// <param name="unmatchedToken">The token the user typed that did not match at the root.</param>
    /// <returns>
    /// A de-duplicated, alphabetically ordered list of qualified command paths (e.g.
    /// <c>agent list</c>). Empty when nothing qualifies — the caller then defers to the
    /// default parse-error output.
    /// </returns>
    internal static IReadOnlyList<string> BuildQualifiedSuggestions(Command root, string unmatchedToken)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (string.IsNullOrWhiteSpace(unmatchedToken))
        {
            return Array.Empty<string>();
        }

        var matches = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var child in root.Subcommands)
        {
            Collect(child, parentPath: string.Empty, unmatchedToken, matches);
        }

        // A suggestion must never be byte-identical to the token the user typed.
        matches.RemoveWhere(path => string.Equals(path, unmatchedToken, StringComparison.Ordinal));

        return matches.Count == 0 ? Array.Empty<string>() : matches.ToArray();
    }

    private static void Collect(Command command, string parentPath, string token, SortedSet<string> matches)
    {
        var currentPath = parentPath.Length == 0 ? command.Name : $"{parentPath} {command.Name}";

        // Only qualified forms (those sitting under a parent) are useful guidance; a bare
        // top-level name equal to the token is exactly the self-referential noise we suppress.
        if (parentPath.Length > 0 && TokenMatches(command, token))
        {
            matches.Add(currentPath);
        }

        foreach (var child in command.Subcommands)
        {
            Collect(child, currentPath, token, matches);
        }
    }

    private static bool TokenMatches(Command command, string token)
    {
        if (string.Equals(command.Name, token, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var alias in command.Aliases)
        {
            if (string.Equals(alias, token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Formats the user-facing guidance line for an unmatched token and its qualified
    /// suggestions. The token is quoted for clarity and every suggestion is quoted so the
    /// space inside a qualified form (<c>agent list</c>) reads as a single command path.
    /// </summary>
    /// <param name="unmatchedToken">The token the user typed.</param>
    /// <param name="suggestions">Non-empty qualified suggestions from <see cref="BuildQualifiedSuggestions"/>.</param>
    /// <returns>A single line suitable for writing to stderr.</returns>
    internal static string FormatMessage(string unmatchedToken, IReadOnlyList<string> suggestions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unmatchedToken);
        ArgumentNullException.ThrowIfNull(suggestions);
        if (suggestions.Count == 0)
        {
            throw new ArgumentException("At least one suggestion is required.", nameof(suggestions));
        }

        var quoted = string.Join(", ", suggestions.Select(s => $"'{s}'"));
        return $"'{unmatchedToken}' is not a botnexus command. Did you mean: {quoted}?";
    }
}
