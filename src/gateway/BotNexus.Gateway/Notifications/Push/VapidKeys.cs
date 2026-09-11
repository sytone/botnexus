using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>
/// The gateway's application-server identity for web push (RFC 8292).
/// </summary>
/// <remarks>
/// One key pair per gateway, generated once and kept. It is not a secret shared with anyone: the
/// PUBLIC half is handed to every browser that subscribes, and a subscription is bound to it - so
/// regenerating the pair silently invalidates every existing subscription. That is why this is
/// persisted rather than derived at startup.
/// </remarks>
public sealed class VapidKeys
{
    /// <summary>The uncompressed P-256 public point, base64url. Given to subscribing browsers.</summary>
    [JsonPropertyName("publicKey")] public required string PublicKey { get; init; }

    /// <summary>The private scalar, base64url. Never leaves the gateway.</summary>
    [JsonPropertyName("privateKey")] public required string PrivateKey { get; init; }

    /// <summary>
    /// Contact for the push service, as a mailto: or https: URI. Required by RFC 8292 so an
    /// operator can be reached about a misbehaving application server.
    /// </summary>
    [JsonPropertyName("subject")] public required string Subject { get; init; }

    /// <summary>Generates a fresh pair.</summary>
    public static VapidKeys Generate(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);

        var publicPoint = new byte[65];
        publicPoint[0] = 0x04;
        parameters.Q.X!.CopyTo(publicPoint, 1 + (32 - parameters.Q.X!.Length));
        parameters.Q.Y!.CopyTo(publicPoint, 33 + (32 - parameters.Q.Y!.Length));

        return new VapidKeys
        {
            PublicKey = Base64Url.Encode(publicPoint),
            PrivateKey = Base64Url.Encode(parameters.D!),
            Subject = subject,
        };
    }

    /// <summary>Rebuilds the signing key.</summary>
    public ECDsa CreateSigningKey()
    {
        var point = Base64Url.Decode(PublicKey);

        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Base64Url.Decode(PrivateKey),
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        });
    }
}

/// <summary>
/// Loads the gateway's VAPID keys from disk, generating them the first time.
/// </summary>
public sealed class VapidKeyStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly string _subject;
    private readonly object _gate = new();
    private VapidKeys? _keys;

    /// <param name="path">Where the pair is kept, typically ~/.botnexus/vapid.json.</param>
    /// <param name="subject">Operator contact, a mailto: or https: URI.</param>
    public VapidKeyStore(string path, string subject)
    {
        _path = path;
        _subject = subject;
    }

    /// <summary>The keys, generated and written on first use.</summary>
    public VapidKeys Keys
    {
        get
        {
            // Double-checked under a lock: two requests arriving together on a fresh gateway must
            // not generate two pairs, because the second would invalidate subscriptions made
            // against the first.
            if (_keys is not null)
                return _keys;

            lock (_gate)
            {
                return _keys ??= LoadOrCreate();
            }
        }
    }

    private VapidKeys LoadOrCreate()
    {
        if (File.Exists(_path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<VapidKeys>(File.ReadAllText(_path));

                if (loaded is not null
                    && !string.IsNullOrWhiteSpace(loaded.PublicKey)
                    && !string.IsNullOrWhiteSpace(loaded.PrivateKey))
                {
                    return loaded;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Fall through and regenerate. Every existing subscription is dead either way -
                // an unreadable key file cannot sign for them.
            }
        }

        var keys = VapidKeys.Generate(_subject);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(keys, Options));

        // The private half is the gateway's identity to every push service it has ever spoken to.
        //
        // Through the central helper, not File.SetUnixFileMode: that call throws
        // PlatformNotSupportedException on Windows, so the raw form needed an OS guard that then
        // left the key UNPROTECTED on Windows rather than applying the equivalent ACL. The helper
        // does the right thing on both (#2392).
        SecureFilePermissions.RestrictToOwner(_path);

        return keys;
    }
}
