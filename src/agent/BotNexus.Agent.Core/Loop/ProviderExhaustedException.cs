namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// Thrown when a turn is short-circuited because the model's provider + auth profile is under an
/// active exhaustion suspension (#3015).
/// </summary>
/// <remarks>
/// A distinct type rather than a generic exception because this failure is categorically different
/// from a provider failure: <b>no provider call was made</b>. The condition was already known, so
/// the turn cost zero round-trips instead of the four (plus 3.5s of backoff) the pre-#3015 loop
/// re-paid on every turn indefinitely.
/// <para>
/// The message is written to be user-facing and actionable, mirroring
/// <c>ProviderAuthenticationException</c>: the loop copies a terminal stream failure's message
/// straight into the assistant message's <c>ErrorMessage</c>, so the operator sees why the turn did
/// not run and that the state clears on its own.
/// </para>
/// </remarks>
public sealed class ProviderExhaustedException : Exception
{
    /// <summary>The provider whose credential is suspended.</summary>
    public string ProviderName { get; }

    /// <summary>Creates the exception for a suspended provider.</summary>
    /// <param name="providerName">The provider whose auth profile is suspended.</param>
    public ProviderExhaustedException(string providerName)
        : base(
            $"Provider '{providerName}' is temporarily suspended for this auth profile after a " +
            "non-transient failure (quota exhausted, billing disabled, or credentials rejected). " +
            "Retrying will not help until the underlying condition is resolved; the suspension " +
            "expires on its own. Top up or fix the credential for this provider, or switch to a " +
            "model whose provider is healthy.")
    {
        ProviderName = providerName;
    }
}
