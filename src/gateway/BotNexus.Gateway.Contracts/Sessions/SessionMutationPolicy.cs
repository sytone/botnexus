using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Single source of truth for the narrow-mutation conflict rules (issue #2132), shared by every
/// <see cref="ISessionStore"/> implementation so the "when is a session mutation refused?" answer
/// cannot drift between the SQLite store (which evaluates it against a column read under its
/// per-session lock) and the File/InMemory stores (which evaluate it against a locked re-read).
/// </summary>
public static class SessionMutationPolicy
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="status"/> is terminal - the session was sealed or
    /// expired deliberately and must not accept further transcript content. A terminal row is the
    /// only reason a transcript append is refused; metadata patches are orthogonal and always
    /// permitted on an existing row.
    /// </summary>
    /// <param name="status">The authoritative persisted status.</param>
    public static bool IsTerminal(SessionStatus status)
        => status is SessionStatus.Sealed or SessionStatus.Expired;

    /// <summary>
    /// Returns <c>true</c> when a lifecycle compare-and-set may proceed, i.e. the authoritative
    /// <paramref name="current"/> status is one the caller declared it was transitioning from.
    /// A transition to the status the row already holds is treated as satisfiable only when the
    /// caller explicitly listed it, so idempotency stays a caller decision rather than an
    /// implicit store behaviour.
    /// </summary>
    /// <param name="expectedStatuses">The statuses the caller considers a legal starting point.</param>
    /// <param name="current">The authoritative persisted status.</param>
    public static bool CanTransition(IReadOnlyList<SessionStatus> expectedStatuses, SessionStatus current)
    {
        ArgumentNullException.ThrowIfNull(expectedStatuses);
        if (expectedStatuses.Count == 0)
            throw new ArgumentException("A lifecycle transition must declare at least one expected status.", nameof(expectedStatuses));

        for (var i = 0; i < expectedStatuses.Count; i++)
        {
            if (expectedStatuses[i] == current)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Applies a metadata patch to a mutable metadata dictionary in place, using the documented
    /// merge semantics: a <c>null</c> value removes the key, any other value adds or overwrites it.
    /// </summary>
    /// <param name="target">The metadata to mutate.</param>
    /// <param name="patch">The patch to merge in.</param>
    public static void ApplyMetadataPatch(
        IDictionary<string, object?> target,
        IReadOnlyDictionary<string, object?> patch)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(patch);

        foreach (var (key, value) in patch)
        {
            if (value is null)
                target.Remove(key);
            else
                target[key] = value;
        }
    }
}
