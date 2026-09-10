using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>
/// Mints and caches the bearer token APNs requires on every request.
/// </summary>
/// <remarks>
/// Apple polices the refresh rate from both directions: a token reused for more than an hour is
/// rejected, and minting a new one more often than every twenty minutes is treated as abuse and
/// answered with TooManyProviderTokenUpdates. So the token is cached and reused, deliberately,
/// rather than signed per request - which is the obvious implementation and the wrong one.
/// </remarks>
public sealed class ApnsTokenProvider(ApnsOptions options, TimeProvider? timeProvider = null)
{
    /// <summary>Comfortably inside Apple's one-hour ceiling and outside its twenty-minute floor.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(45);

    private readonly ApnsOptions _options = options;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _sync = new();

    private string? _token;
    private DateTimeOffset _mintedAt;

    /// <summary>The current bearer token, minted on first use and refreshed when it ages out.</summary>
    public string GetToken()
    {
        lock (_sync)
        {
            var now = _time.GetUtcNow();

            if (_token is not null && now - _mintedAt < Lifetime)
                return _token;

            _token = Mint(now);
            _mintedAt = now;

            return _token;
        }
    }

    private string Mint(DateTimeOffset now)
    {
        var header = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["alg"] = "ES256",
            ["kid"] = _options.KeyId!,
        }));

        var claims = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = _options.TeamId!,
            ["iat"] = now.ToUnixTimeSeconds(),
        }));

        var signingInput = $"{header}.{claims}";

        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(_options.PrivateKeyPath!));

        // JWS wants the raw r||s pair. The DER encoding SignData produces by default yields a
        // token Apple rejects as InvalidProviderToken, with nothing to say which part was wrong.
        var signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return $"{signingInput}.{Base64Url.Encode(signature)}";
    }
}
