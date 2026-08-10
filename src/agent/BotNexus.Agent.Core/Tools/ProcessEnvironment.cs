namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// Single merge seam for caller-supplied child-process environment overrides.
/// <para>
/// Every spawn site (the <c>exec</c> tool, the MCP stdio transport) routes its override
/// dictionary through <see cref="Merge"/> rather than writing
/// <c>startInfo.Environment[key] = value</c> directly. Writing directly leaks the CALLER
/// dictionary's comparer into the merge: on Windows the process environment block is
/// case-insensitive, so a caller passing <c>path</c> over an inherited <c>PATH</c> either
/// produced two conflicting entries for one logical variable or was silently ignored, with
/// no warning (#2892). Centralising the merge means the platform casing rule is decided
/// once instead of being re-derived - and re-broken - per site.
/// </para>
/// </summary>
public static class ProcessEnvironment
{
    /// <summary>
    /// The comparer that matches the host's own environment-block semantics: Windows treats
    /// variable names case-insensitively, POSIX treats them as distinct byte sequences.
    /// Exposed so callers and tests can reason about the active rule explicitly rather than
    /// guessing from the running platform.
    /// </summary>
    public static StringComparer KeyComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Applies <paramref name="overrides"/> onto <paramref name="target"/> - normally a
    /// <see cref="System.Diagnostics.ProcessStartInfo.Environment"/> block already seeded from the
    /// parent process - so that an override replaces the inherited variable of the same name under
    /// the platform's own casing rule. Any pre-existing entry whose key differs only by casing is
    /// removed first, so the child receives exactly one entry per logical variable on Windows.
    /// </summary>
    /// <param name="target">The environment block being built; mutated in place.</param>
    /// <param name="overrides">Caller-supplied overrides, applied in enumeration order.</param>
    /// <param name="keyComparer">
    /// Collision rule to apply. Defaults to <see cref="KeyComparer"/>. Pass an explicit comparer
    /// only to model a platform other than the host - tests do this to cover both branches on one machine.
    /// </param>
    /// <param name="valueTransform">
    /// Optional per-value projection applied before the value is written, letting a site resolve
    /// its own placeholder syntax (the MCP transport's <c>${env:NAME}</c>) without reintroducing
    /// a second merge loop.
    /// </param>
    public static void Merge(
        IDictionary<string, string?> target,
        IEnumerable<KeyValuePair<string, string>> overrides,
        StringComparer? keyComparer = null,
        Func<string, string>? valueTransform = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(overrides);

        var comparer = keyComparer ?? KeyComparer;

        foreach (var (key, value) in overrides)
        {
            // Drop inherited entries that collide under the platform rule but are not the exact
            // key being written. Without this the child sees both PATH=Y and path=X on Windows.
            var stale = target.Keys
                .Where(existing => !string.Equals(existing, key, StringComparison.Ordinal)
                                   && comparer.Equals(existing, key))
                .ToList();

            foreach (var staleKey in stale)
            {
                target.Remove(staleKey);
            }

            target[key] = valueTransform is null ? value : valueTransform(value);
        }
    }
}
