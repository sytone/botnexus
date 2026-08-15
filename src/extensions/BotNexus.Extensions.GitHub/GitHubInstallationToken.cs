namespace BotNexus.Extensions.GitHub;

/// <summary>
/// A minted GitHub App installation token and the instant it stops being usable.
/// </summary>
/// <remarks>
/// This type is the seam between a token <i>source</i> and the caching provider. It is deliberately
/// NOT part of the provider's public result surface (#2732 AC4): callers of
/// <see cref="IGitHubCredentialProvider"/> never receive one. <see cref="ToString"/> is overridden so
/// that an accidental interpolation into a log line or an exception message cannot leak the secret —
/// the default record <c>ToString</c> would print every property, including <see cref="Value"/>.
/// </remarks>
/// <param name="Value">The opaque installation token. Never log, never serialise, never export.</param>
/// <param name="ExpiresAt">The instant GitHub stops honouring <paramref name="Value"/>.</param>
public sealed record GitHubInstallationToken(string Value, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// Renders the token as a redacted placeholder plus its expiry. Overriding this is the whole
    /// point: records print all their properties by default, so the compiler-generated
    /// <c>ToString</c> would put the secret into any interpolated string.
    /// </summary>
    public override string ToString() => $"GitHubInstallationToken {{ Value = [redacted], ExpiresAt = {ExpiresAt:O} }}";
}
