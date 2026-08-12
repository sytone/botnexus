namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// Records time-bounded suspensions for a provider + auth profile pair after a non-transient
/// exhaustion failure (#3015).
/// </summary>
/// <remarks>
/// <para>
/// The scope is deliberately <b>provider + auth profile</b> -- never global, never per-session.
/// Global would let one exhausted credential black-hole every provider on the instance; per-session
/// would forget the condition the moment the session ended, which is precisely the amnesia #3015
/// exists to fix (the pre-#3015 state lived entirely in a local <c>attempt</c> counter and died with
/// the call, so every turn re-paid the full four-round-trip discovery cost).
/// </para>
/// <para>
/// Suspensions <b>expire</b>. An exhausted quota is durable but not permanent: a plan is topped up,
/// a billing hold is released, a monthly window rolls over. A suspension that never expired would
/// require a restart to clear and would convert a recoverable condition into an outage.
/// </para>
/// <para>
/// Only <see cref="ProviderFailureClass.Exhausted"/> may reach this type. A provider
/// <em>overload</em> is <see cref="ProviderFailureClass.Transient"/> and must not cool an auth
/// profile -- the credential is fine, the provider is busy (per the upstream review trail on
/// OpenClaw <c>77d89b2fa843</c>).
/// </para>
/// </remarks>
public interface IProviderSuspensionRegistry
{
    /// <summary>
    /// Records a suspension for the given provider and auth profile, expiring after <paramref name="duration"/>.
    /// </summary>
    /// <param name="provider">The provider that reported exhaustion (e.g. <c>github-copilot</c>).</param>
    /// <param name="authProfile">The auth profile whose credential is exhausted.</param>
    /// <param name="duration">How long the suspension remains in force.</param>
    /// <param name="reason">The provider error text, retained for diagnosis.</param>
    void Suspend(string provider, string authProfile, TimeSpan duration, string reason);

    /// <summary>
    /// Returns <see langword="true"/> when the given provider + auth profile pair is currently
    /// suspended. An expired suspension returns <see langword="false"/>.
    /// </summary>
    /// <param name="provider">The provider to test.</param>
    /// <param name="authProfile">The auth profile to test.</param>
    bool IsSuspended(string provider, string authProfile);
}

/// <summary>
/// In-memory <see cref="IProviderSuspensionRegistry"/> keyed on the composite
/// (provider, auth profile) pair.
/// </summary>
/// <remarks>
/// The clock is injectable so expiry is testable without sleeping -- an expiry test that waits on
/// wall-clock time is either slow or flaky, and usually both.
/// </remarks>
public sealed class ProviderSuspensionRegistry : IProviderSuspensionRegistry
{
    /// <summary>
    /// Default suspension window applied when the loop records an exhaustion (#3015). Long enough
    /// that a wedged credential stops taxing every turn, short enough that a topped-up plan
    /// recovers on its own without an operator restart.
    /// </summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(15);

    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();

    // Key is the composite scope, so a second auth profile on the SAME provider -- and the same
    // profile on a DIFFERENT provider -- are independent entries and cannot be cooled by each other.
    private readonly Dictionary<(string Provider, string AuthProfile), SuspensionEntry> _entries =
        new(ScopeComparer.Instance);

    /// <summary>Creates a registry using the system clock.</summary>
    public ProviderSuspensionRegistry()
        : this(() => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>Creates a registry with an injectable clock (tests, deterministic expiry).</summary>
    /// <param name="clock">Supplies the current instant.</param>
    public ProviderSuspensionRegistry(Func<DateTimeOffset> clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public void Suspend(string provider, string authProfile, TimeSpan duration, string reason)
    {
        if (string.IsNullOrWhiteSpace(provider) || duration <= TimeSpan.Zero)
        {
            return;
        }

        var key = (provider, authProfile ?? string.Empty);
        lock (_sync)
        {
            _entries[key] = new SuspensionEntry(_clock() + duration, reason);
        }
    }

    /// <inheritdoc />
    public bool IsSuspended(string provider, string authProfile)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return false;
        }

        var key = (provider, authProfile ?? string.Empty);
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return false;
            }

            if (entry.ExpiresAt <= _clock())
            {
                // Expired entries are evicted on read so a long-lived registry cannot grow without
                // bound across credential rotations.
                _entries.Remove(key);
                return false;
            }

            return true;
        }
    }

    private readonly record struct SuspensionEntry(DateTimeOffset ExpiresAt, string Reason);

    private sealed class ScopeComparer : IEqualityComparer<(string Provider, string AuthProfile)>
    {
        public static readonly ScopeComparer Instance = new();

        public bool Equals((string Provider, string AuthProfile) x, (string Provider, string AuthProfile) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Provider, y.Provider)
               && StringComparer.Ordinal.Equals(x.AuthProfile, y.AuthProfile);

        public int GetHashCode((string Provider, string AuthProfile) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Provider),
                StringComparer.Ordinal.GetHashCode(obj.AuthProfile));
    }
}
