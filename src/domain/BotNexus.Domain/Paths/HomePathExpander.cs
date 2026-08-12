namespace BotNexus.Domain.Paths;

/// <summary>
/// The single home-path (<c>~</c>) expansion implementation for BotNexus.
/// </summary>
/// <remarks>
/// <para>
/// This type exists because the same twelve-line expansion helper had been copied - and had drifted -
/// into four separate places (<c>SubAgentWorkspaceRootResolver</c>, <c>WorldDescriptorBuilder</c>,
/// <c>LocationProbe</c> and <c>DefaultPathValidator</c>). Two of them fell back to the <c>HOME</c>
/// environment variable when <see cref="Environment.SpecialFolder.UserProfile"/> was empty and two did
/// not, so the same configured string could resolve to a rooted-at-empty path on Linux depending only on
/// which code path happened to read it. Copying rather than extracting is the root cause, so the fix is
/// one implementation plus an architecture fence forbidding a second one (see issue #3013).
/// </para>
/// <para>
/// Expansion semantics, which are the union of the four originals:
/// </para>
/// <list type="bullet">
///   <item><description>A path that does not begin with <c>~</c> is returned unchanged.</description></item>
///   <item><description>A bare <c>~</c> becomes the home directory.</description></item>
///   <item><description><c>~</c> followed by a directory separator has the <c>~</c> replaced by the home directory.</description></item>
///   <item><description>Anything else - notably the <c>~user</c> form - is returned unchanged, because
///   BotNexus has never resolved another user's home directory and silently guessing one would be worse
///   than leaving the literal in place.</description></item>
/// </list>
/// <para>
/// "Directory separator" means <see cref="Path.DirectorySeparatorChar"/> or
/// <see cref="Path.AltDirectorySeparatorChar"/>, which is deliberately platform-dependent and matches
/// every original copy. On Windows both <c>~/x</c> and <c>~\x</c> expand; on Unix only <c>~/x</c> does,
/// because a backslash is a legal character in a Unix file name and expanding it would corrupt a valid
/// literal path.
/// </para>
/// </remarks>
public static class HomePathExpander
{
    /// <summary>
    /// Expands a leading <c>~</c> to the current user's home directory, tolerating an unknown home.
    /// </summary>
    /// <param name="path">The raw path, which may be <see langword="null"/> or empty.</param>
    /// <returns>
    /// The expanded path, or <paramref name="path"/> unchanged when it does not start with an
    /// expandable <c>~</c> form.
    /// </returns>
    /// <remarks>
    /// When the home directory cannot be determined this returns the path expanded against an empty
    /// home - the historical behaviour of the <c>SubAgentWorkspaceRootResolver</c>,
    /// <c>LocationProbe</c> and <c>DefaultPathValidator</c> copies. Callers that cannot tolerate that
    /// should use <see cref="ExpandRequired"/> instead.
    /// </remarks>
    public static string Expand(string path)
    {
        return ExpandCore(path, requireHome: false);
    }

    /// <summary>
    /// Expands a leading <c>~</c> to the current user's home directory, failing when the home directory
    /// cannot be determined.
    /// </summary>
    /// <param name="path">The raw path, which may be <see langword="null"/> or empty.</param>
    /// <returns>The expanded path, or <paramref name="path"/> unchanged when it does not start with <c>~</c>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="path"/> starts with <c>~</c> and neither
    /// <see cref="Environment.SpecialFolder.UserProfile"/> nor the <c>HOME</c> environment variable
    /// yields a home directory. This mirrors the <c>WorldDescriptorBuilder</c> copy, which preferred a
    /// loud failure over silently producing a path rooted at the empty string.
    /// </exception>
    public static string ExpandRequired(string path)
    {
        return ExpandCore(path, requireHome: true);
    }

    /// <summary>
    /// Resolves the current user's home directory, preferring
    /// <see cref="Environment.SpecialFolder.UserProfile"/> and falling back to the <c>HOME</c>
    /// environment variable.
    /// </summary>
    /// <returns>The home directory, or an empty string when it cannot be determined.</returns>
    /// <remarks>
    /// The <c>HOME</c> fallback is the behaviour two of the four original copies had and two lacked.
    /// Consolidating on the fallback is strictly safer: on Linux <c>UserProfile</c> can legitimately be
    /// empty, and without the fallback the expansion produced a path rooted at the empty string.
    /// </remarks>
    public static string GetHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        }

        return home;
    }

    /// <summary>
    /// Indicates whether <paramref name="path"/> begins with a <c>~</c> that
    /// <see cref="Expand(string)"/> would act on.
    /// </summary>
    /// <param name="path">The raw path to inspect.</param>
    /// <returns><see langword="true"/> when the path starts with <c>~</c>; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// Used by callers that need to phrase an error message in terms of the <c>~</c> the user actually
    /// typed rather than the expanded absolute path, which is meaningless to them.
    /// </remarks>
    public static bool StartsWithHomeToken(string? path)
    {
        return !string.IsNullOrEmpty(path) && path[0] == '~';
    }

    private static string ExpandCore(string path, bool requireHome)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~')
        {
            return path;
        }

        var home = GetHomeDirectory();
        if (requireHome && string.IsNullOrWhiteSpace(home))
        {
            throw new InvalidOperationException("Unable to determine user home directory.");
        }

        if (path.Length == 1)
        {
            return home;
        }

        var first = path[1];
        if (first == Path.DirectorySeparatorChar || first == Path.AltDirectorySeparatorChar)
        {
            return Path.Combine(home, path[2..]);
        }

        return path;
    }
}
