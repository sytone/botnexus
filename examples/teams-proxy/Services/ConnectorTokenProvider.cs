using Azure.Core;
using BotNexus.TeamsProxy.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.TeamsProxy.Services;

public sealed class ConnectorTokenProvider
{
    private static readonly TimeSpan RefreshOffset = TimeSpan.FromMinutes(5);

    private readonly TokenCredential _credential;
    private readonly TeamsProxyOptions _options;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private AccessToken _cachedToken;

    public ConnectorTokenProvider(
        TokenCredential credential,
        IOptions<TeamsProxyOptions> options)
    {
        _credential = credential;
        _options = options.Value;
    }

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cachedToken.Token)
            && _cachedToken.ExpiresOn > DateTimeOffset.UtcNow.Add(RefreshOffset))
        {
            return _cachedToken.Token;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedToken.Token)
                && _cachedToken.ExpiresOn > DateTimeOffset.UtcNow.Add(RefreshOffset))
            {
                return _cachedToken.Token;
            }

            _cachedToken = await _credential.GetTokenAsync(
                new TokenRequestContext([_options.ConnectorApiScope]),
                cancellationToken);

            return _cachedToken.Token;
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
