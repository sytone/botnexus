using System.Security.Cryptography;
using System.Text;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>
/// Encrypts a push payload for one subscription, per RFC 8291 (aes128gcm).
/// </summary>
/// <remarks>
/// <para>
/// The push service is an untrusted relay: Google, Mozilla and Apple all forward the message
/// without being able to read it. That is the whole point of this file - the payload is encrypted
/// to a key only the subscribing browser holds, so a notification saying which agent failed does
/// not become readable by whoever operates the relay.
/// </para>
/// <para>
/// Hand-written against the RFC rather than taken from a package. The dependency-free version is
/// about a hundred lines of standard primitives that .NET already ships, and it is verified
/// against the worked example in RFC 8291 section 5 - including by decrypting the RFC's own
/// ciphertext, which pins the key derivation to the spec rather than to itself.
/// </para>
/// </remarks>
public static class WebPushEncryptor
{
    private const int SaltLength = 16;
    private const int KeyLength = 16;
    private const int NonceLength = 12;
    private const int PublicKeyLength = 65;

    /// <summary>Record size written into the header. One record, so it only has to exceed the payload.</summary>
    private const int RecordSize = 4096;

    private static readonly byte[] KeyInfoPrefix = Encoding.ASCII.GetBytes("WebPush: info\0");
    private static readonly byte[] CekInfo = Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0");
    private static readonly byte[] NonceInfo = Encoding.ASCII.GetBytes("Content-Encoding: nonce\0");

    /// <summary>
    /// Encrypts <paramref name="payload"/> for a subscription's keys, returning the request body.
    /// </summary>
    /// <param name="uaPublicKey">The subscription's p256dh key: an uncompressed P-256 point.</param>
    /// <param name="authSecret">The subscription's 16-byte auth secret.</param>
    /// <param name="payload">The plaintext to deliver.</param>
    public static byte[] Encrypt(byte[] uaPublicKey, byte[] authSecret, byte[] payload)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        return Encrypt(uaPublicKey, authSecret, payload, ephemeral, RandomNumberGenerator.GetBytes(SaltLength));
    }

    /// <summary>
    /// Encrypts with a caller-supplied ephemeral key and salt. Exists so the RFC's worked example
    /// can be reproduced exactly; production callers use the overload that generates both.
    /// </summary>
    internal static byte[] Encrypt(
        byte[] uaPublicKey,
        byte[] authSecret,
        byte[] payload,
        ECDiffieHellman ephemeral,
        byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(uaPublicKey);
        ArgumentNullException.ThrowIfNull(authSecret);
        ArgumentNullException.ThrowIfNull(payload);

        if (uaPublicKey.Length != PublicKeyLength)
            throw new ArgumentException(
                $"A p256dh key must be {PublicKeyLength} uncompressed bytes, got {uaPublicKey.Length}.",
                nameof(uaPublicKey));

        var asPublicKey = ExportUncompressed(ephemeral);
        var sharedSecret = DeriveSharedSecret(ephemeral, uaPublicKey);

        // The auth secret is the HKDF salt here, NOT the message salt: this step binds the keys to
        // the subscription, and the message salt only enters at the next one.
        var keyInfo = Concat(KeyInfoPrefix, uaPublicKey, asPublicKey);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, authSecret, keyInfo);

        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeyLength, salt, CekInfo);
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, NonceLength, salt, NonceInfo);

        // 0x02 marks the last (and here only) record. A 0x01 delimiter would tell the browser to
        // expect another record and the whole message would be rejected.
        var padded = new byte[payload.Length + 1];
        payload.CopyTo(padded, 0);
        padded[^1] = 0x02;

        var ciphertext = new byte[padded.Length];
        var tag = new byte[16];

        using (var aes = new AesGcm(cek, tag.Length))
        {
            aes.Encrypt(nonce, padded, ciphertext, tag);
        }

        // Header: salt | record size | key id length | key id (the sender's public key), then the
        // ciphertext with its tag appended.
        var body = new byte[SaltLength + 4 + 1 + PublicKeyLength + ciphertext.Length + tag.Length];
        var at = 0;

        salt.CopyTo(body, at); at += SaltLength;
        body[at++] = unchecked((byte)(RecordSize >> 24));
        body[at++] = unchecked((byte)(RecordSize >> 16));
        body[at++] = unchecked((byte)(RecordSize >> 8));
        body[at++] = unchecked((byte)RecordSize);
        body[at++] = PublicKeyLength;
        asPublicKey.CopyTo(body, at); at += PublicKeyLength;
        ciphertext.CopyTo(body, at); at += ciphertext.Length;
        tag.CopyTo(body, at);

        return body;
    }

    /// <summary>
    /// Decrypts a body produced by <see cref="Encrypt(byte[], byte[], byte[])"/>.
    /// </summary>
    /// <remarks>
    /// Not used in production - the browser does this. It exists so the implementation can be
    /// checked against the RFC's own ciphertext, which is the only way to prove the derivation
    /// follows the spec rather than merely agreeing with itself.
    /// </remarks>
    internal static byte[] Decrypt(byte[] body, ECDiffieHellman uaKey, byte[] authSecret)
    {
        var salt = body[..SaltLength];
        var idLength = body[SaltLength + 4];
        var asPublicKey = body.AsSpan(SaltLength + 5, idLength).ToArray();
        var ciphertextWithTag = body.AsSpan(SaltLength + 5 + idLength).ToArray();

        var uaPublicKey = ExportUncompressed(uaKey);
        var sharedSecret = DeriveSharedSecret(uaKey, asPublicKey);

        var keyInfo = Concat(KeyInfoPrefix, uaPublicKey, asPublicKey);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, authSecret, keyInfo);

        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeyLength, salt, CekInfo);
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, NonceLength, salt, NonceInfo);

        var tag = ciphertextWithTag[^16..];
        var ciphertext = ciphertextWithTag[..^16];
        var padded = new byte[ciphertext.Length];

        using (var aes = new AesGcm(cek, tag.Length))
        {
            aes.Decrypt(nonce, ciphertext, tag, padded);
        }

        // Strip the record delimiter and any zero padding that followed it.
        var end = padded.Length - 1;
        while (end >= 0 && padded[end] == 0x00) end--;

        return padded[..end];
    }

    /// <summary>
    /// Whether a p256dh value is a usable subscriber key: right length, and an actual point on the
    /// P-256 curve.
    /// </summary>
    /// <remarks>
    /// Length alone is not enough, and the difference is not academic. 65 bytes that are not on the
    /// curve are accepted by every length check and then throw inside the key agreement on every
    /// single notification, forever, while the browser that sent them believes it is subscribed.
    /// Found exactly that way: a hand-made subscription with a random 65-byte "key" was stored
    /// happily and then failed on the next notification raised.
    /// </remarks>
    public static bool IsValidSubscriberKey(byte[] uaPublicKey)
    {
        if (uaPublicKey is not { Length: PublicKeyLength } || uaPublicKey[0] != 0x04)
            return false;

        try
        {
            using var _ = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = uaPublicKey.AsSpan(1, 32).ToArray(),
                    Y = uaPublicKey.AsSpan(33, 32).ToArray(),
                },
            });

            return true;
        }
        catch (CryptographicException)
        {
            // Not a point on the curve.
            return false;
        }
    }

    private static byte[] DeriveSharedSecret(ECDiffieHellman ours, byte[] theirPublicKey)
    {
        using var theirs = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = theirPublicKey.AsSpan(1, 32).ToArray(),
                Y = theirPublicKey.AsSpan(33, 32).ToArray(),
            },
        });

        // The raw x-coordinate, not a hashed secret: RFC 8291 feeds it into HKDF itself.
        return ours.DeriveRawSecretAgreement(theirs.PublicKey);
    }

    private static byte[] ExportUncompressed(ECDiffieHellman key)
    {
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        var bytes = new byte[PublicKeyLength];

        bytes[0] = 0x04;
        q.X!.CopyTo(bytes, 1 + (32 - q.X!.Length));
        q.Y!.CopyTo(bytes, 33 + (32 - q.Y!.Length));

        return bytes;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var at = 0;

        foreach (var part in parts)
        {
            part.CopyTo(result, at);
            at += part.Length;
        }

        return result;
    }
}
