namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// The retry lane a provider failure belongs to (#3015).
/// </summary>
/// <remarks>
/// Before #3015 the loop asked a single boolean question -- "is this transient?" -- and every
/// <see langword="true"/> answer bought the same four attempts and the same exponential backoff.
/// A hard quota/billing/credential failure therefore cost four provider round-trips plus 3.5s of
/// backoff <em>per turn, forever</em>, and logged "transient, retrying" for a condition that will
/// never clear on its own.
/// <para>
/// The tri-state exists because the two failure families need opposite treatment, and a bool cannot
/// express the difference: a 503 wants patience, an exhausted quota wants memory.
/// </para>
/// </remarks>
public enum ProviderFailureClass
{
    /// <summary>
    /// Not retriable and not an exhaustion condition -- a malformed request, an unknown model, or
    /// any other failure the classifier does not recognise. Fails immediately with no suspension.
    /// This is the pre-#3015 <c>IsTransient == false</c> path and behaves identically.
    /// </summary>
    Terminal = 0,

    /// <summary>
    /// A transient provider-side or transport failure (5xx, overload, socket abort, timeout).
    /// Retried with the existing attempt budget and backoff, byte-for-byte as before #3015.
    /// A transient failure must NEVER create an auth-profile suspension: provider overload is a
    /// property of the provider's capacity at that instant, not of the caller's credentials.
    /// </summary>
    Transient = 1,

    /// <summary>
    /// A non-transient exhaustion condition: quota exhausted, billing disabled, credentials
    /// rejected. Retrying spends the budget to learn nothing, so the loop fails after exactly one
    /// attempt and records a time-bounded suspension against the provider + auth profile so
    /// subsequent turns short-circuit instead of re-burning the retry budget.
    /// </summary>
    Exhausted = 2,
}
