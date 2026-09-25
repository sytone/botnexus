using System.Net.Http.Headers;
using Azure.Core;

namespace BotNexus.Agent.Providers.MicrosoftFoundry;

/// <summary>
/// Resolves and caches authentication for one named Foundry endpoint instance without exposing raw-token callbacks.
/// </summary>
public sealed class MicrosoftFoundryAuthenticationResolver
{
    /// <summary>
    /// The only Entra audience accepted by the Microsoft Foundry Responses wire provider.
    /// </summary>
    public const string EntraAudience = "https://ai.azure.com/.default";

    private static readonly TokenRequestContext TokenContext = new([EntraAudience]);
    private static readonly TimeSpan RefreshBeforeExpiry = TimeSpan.FromMinutes(5);
    private readonly MicrosoftFoundryAuthentication _authentication;
    private readonly TokenCredential? _credential;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private AccessToken? _cachedToken;

    /// <summary>
    /// Creates an instance-bound resolver. The optional credential is a test seam and is ignored by API-key mode.
    /// </summary>
    public MicrosoftFoundryAuthenticationResolver(
        MicrosoftFoundryAuthentication authentication,
        TokenCredential? credential = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _credential = credential ?? authentication.Credential;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Attaches either an API key or a cached Entra bearer token to an already validated request.
    /// </summary>
    public async ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Remove("Authorization");
        request.Headers.Remove("api-key");

        if (_authentication.ApiKeyValue is { } apiKey)
        {
            request.Headers.TryAddWithoutValidation("api-key", apiKey);
            return;
        }

        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private async ValueTask<AccessToken> GetTokenAsync(CancellationToken cancellationToken)
    {
        var cached = _cachedToken;
        if (IsFresh(cached))
            return cached.GetValueOrDefault();

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _cachedToken;
            if (IsFresh(cached))
                return cached.GetValueOrDefault();

            var credential = _credential ?? throw new InvalidOperationException("Entra authentication requires a TokenCredential.");
            var acquired = await credential.GetTokenAsync(TokenContext, cancellationToken).ConfigureAwait(false);
            _cachedToken = acquired;
            return acquired;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private bool IsFresh(AccessToken? token) =>
        token is { } value && value.ExpiresOn > _utcNow().Add(RefreshBeforeExpiry);
}
