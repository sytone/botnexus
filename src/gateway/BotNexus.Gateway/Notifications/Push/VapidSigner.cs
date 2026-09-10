using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>
/// Builds the signed Authorization header a push service demands (RFC 8292).
/// </summary>
/// <remarks>
/// The token proves the request comes from the application server the subscription was created
/// against, which is what stops anyone who learns an endpoint URL from pushing to it. It is
/// audience-scoped to one push service and short-lived, so a captured header is of little use.
/// </remarks>
public sealed class VapidSigner(VapidKeys keys, TimeProvider? timeProvider = null)
{
    // Twelve hours. RFC 8292 caps this at 24; half that leaves room for clock skew in either
    // direction without minting a token per message.
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private readonly VapidKeys _keys = keys;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Builds the header value for one push endpoint.
    /// </summary>
    /// <param name="endpoint">The subscription endpoint; only its origin is signed over.</param>
    public string CreateAuthorizationHeader(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var audience = endpoint.GetLeftPart(UriPartial.Authority);
        var expiry = _time.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds();

        var header = Base64Url.Encode(Encoding.UTF8.GetBytes("""{"typ":"JWT","alg":"ES256"}"""));
        var claims = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = audience,
            ["exp"] = expiry,
            ["sub"] = _keys.Subject,
        }));

        var signingInput = $"{header}.{claims}";

        using var key = _keys.CreateSigningKey();

        // JWS wants the raw r||s pair, not the DER encoding SignData produces by default. Getting
        // this wrong yields a token every push service rejects with 401.
        var signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return $"vapid t={signingInput}.{Base64Url.Encode(signature)}, k={_keys.PublicKey}";
    }
}
