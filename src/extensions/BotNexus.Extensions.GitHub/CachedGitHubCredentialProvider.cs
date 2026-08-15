using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// The platform-owned GitHub credential provider (#2732): mints an installation token on first use
/// and reuses it until it expires, then transparently refreshes. No agent turn is involved in the
/// refresh — the expiry check and the re-mint happen inside a single <see cref="AuthenticateAsync"/>
/// call.
/// </summary>
/// <remarks>
/// <para><b>Why the clock is injected.</b> Expiry is evaluated against an injected
/// <see cref="TimeProvider"/> rather than <c>DateTimeOffset.UtcNow</c> so the expired-token path is
/// testable by advancing a fake clock instead of sleeping for an hour.</para>
/// <para><b>Why nothing here logs the token.</b> Every log statement in this type takes only the
/// expiry instant. The token is held in a private field, written into an
/// <see cref="AuthenticationHeaderValue"/>, and never returned, stringified, or exported to the
/// process environment.</para>
/// </remarks>
public sealed class CachedGitHubCredentialProvider : IGitHubCredentialProvider
{
    private readonly IGitHubInstallationTokenSource _source;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _expirySkew;
    private readonly ILogger<CachedGitHubCredentialProvider>? _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private GitHubInstallationToken? _cached;

    /// <summary>Creates a provider over a token source, a clock, and an optional expiry skew.</summary>
    public CachedGitHubCredentialProvider(
        IGitHubInstallationTokenSource source,
        TimeProvider? timeProvider = null,
        TimeSpan? expirySkew = null,
        ILogger<CachedGitHubCredentialProvider>? logger = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _expirySkew = expirySkew ?? TimeSpan.Zero;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task AuthenticateAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
    }

    /// <summary>
    /// Returns a usable token, minting one only when there is no cached token or the cached one has
    /// expired. Internal (not public) so the secret stays off the provider's public result surface
    /// while the caching policy itself remains directly testable.
    /// </summary>
    internal async Task<GitHubInstallationToken> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var current = _cached;
        if (current is not null && !IsExpired(current))
        {
            return current;
        }

        // Serialise refreshes so a burst of concurrent callers mints once, not N times.
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current = _cached;
            if (current is not null && !IsExpired(current))
            {
                return current;
            }

            var minted = await _source.MintAsync(cancellationToken).ConfigureAwait(false);
            _cached = minted;

            // Expiry only. Logging the value — or the token object without its redacting ToString —
            // is exactly the leak AC4 pins.
            _logger?.LogDebug(
                "Minted a GitHub App installation token valid until {ExpiresAt:O}.",
                minted.ExpiresAt);

            return minted;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private bool IsExpired(GitHubInstallationToken token) =>
        _timeProvider.GetUtcNow() >= token.ExpiresAt - _expirySkew;
}
