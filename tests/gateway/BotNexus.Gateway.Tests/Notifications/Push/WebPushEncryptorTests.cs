using System.Security.Cryptography;
using System.Text;
using BotNexus.Gateway.Notifications.Push;

namespace BotNexus.Gateway.Tests.Notifications.Push;

/// <summary>
/// Pins the push payload encryption to RFC 8291.
/// </summary>
/// <remarks>
/// Crypto that only agrees with itself is worthless: a round-trip test passes just as happily on
/// an implementation that derives the wrong keys consistently, and the failure would only show up
/// as a browser silently dropping every notification. So the anchor here is the worked example in
/// RFC 8291 section 5 - decrypting the RFC's own ciphertext proves the derivation matches the
/// spec, and reproducing its body byte for byte proves the encrypt direction does too.
/// </remarks>
public sealed class WebPushEncryptorTests
{
    // RFC 8291, section 5.
    private const string Plaintext = "When I grow up, I want to be a watermelon";
    private const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string UaPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    private const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string AsPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string AsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string ExpectedBody =
        "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS"
        + "6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Q"
        + "ulcy4a-fN";

    private static ECDiffieHellman KeyFrom(string publicKey, string privateKey)
    {
        var q = Base64Url.Decode(publicKey);

        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Base64Url.Decode(privateKey),
            Q = new ECPoint { X = q[1..33], Y = q[33..65] },
        });
    }

    // The load-bearing direction: this ciphertext was produced by someone else's implementation,
    // so recovering the plaintext from it proves the key derivation follows the RFC.
    [Fact]
    public void Decrypts_the_ciphertext_from_the_RFC()
    {
        using var uaKey = KeyFrom(UaPublic, UaPrivate);

        var recovered = WebPushEncryptor.Decrypt(
            Base64Url.Decode(ExpectedBody), uaKey, Base64Url.Decode(AuthSecret));

        Assert.Equal(Plaintext, Encoding.UTF8.GetString(recovered));
    }

    [Fact]
    public void Reproduces_the_body_from_the_RFC()
    {
        using var asKey = KeyFrom(AsPublic, AsPrivate);

        var body = WebPushEncryptor.Encrypt(
            Base64Url.Decode(UaPublic),
            Base64Url.Decode(AuthSecret),
            Encoding.UTF8.GetBytes(Plaintext),
            asKey,
            Base64Url.Decode(Salt));

        Assert.Equal(ExpectedBody, Base64Url.Encode(body));
    }

    [Fact]
    public void Round_trips_a_notification_sized_payload()
    {
        using var uaKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var uaPublic = uaKey.ExportSubjectPublicKeyInfo();
        var q = uaKey.ExportParameters(false).Q;
        var uncompressed = new byte[65];
        uncompressed[0] = 0x04;
        q.X!.CopyTo(uncompressed, 1);
        q.Y!.CopyTo(uncompressed, 33);

        var auth = RandomNumberGenerator.GetBytes(16);
        var payload = Encoding.UTF8.GetBytes(
            """{"title":"Agent 'assistant' run failed","body":"The provider returned 529."}""");

        var body = WebPushEncryptor.Encrypt(uncompressed, auth, payload);

        Assert.Equal(payload, WebPushEncryptor.Decrypt(body, uaKey, auth));
        Assert.NotEqual(uaPublic, body);
    }

    // Found in production, not in review: a hand-made subscription whose p256dh was 65 random
    // bytes passed the length check the endpoint had, was stored, and then threw inside the key
    // agreement on the next notification raised - and would have done so on every notification
    // after it, while the subscriber believed it was subscribed.
    [Fact]
    public void Rejects_a_key_that_is_the_right_length_but_not_on_the_curve()
    {
        var notOnCurve = new byte[65];
        notOnCurve[0] = 0x04;
        RandomNumberGenerator.Fill(notOnCurve.AsSpan(1));

        Assert.False(WebPushEncryptor.IsValidSubscriberKey(notOnCurve));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(66)]
    [InlineData(0)]
    public void Rejects_a_key_of_the_wrong_length(int length)
    {
        var key = new byte[length];
        if (length > 0) key[0] = 0x04;

        Assert.False(WebPushEncryptor.IsValidSubscriberKey(key));
    }

    // A compressed point is well-formed but not what the Push API produces, and the rest of this
    // file assumes 65 uncompressed bytes.
    [Fact]
    public void Rejects_a_key_that_is_not_uncompressed()
    {
        var compressed = new byte[65];
        compressed[0] = 0x02;

        Assert.False(WebPushEncryptor.IsValidSubscriberKey(compressed));
    }

    [Fact]
    public void Accepts_a_real_subscriber_key()
    {
        Assert.True(WebPushEncryptor.IsValidSubscriberKey(Base64Url.Decode(UaPublic)));
    }

    // Every message must use a fresh ephemeral key and salt, or two notifications to the same
    // subscription would reuse a key/nonce pair - the one mistake AES-GCM does not forgive.
    [Fact]
    public void Uses_a_fresh_key_and_salt_for_every_message()
    {
        using var uaKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = uaKey.ExportParameters(false).Q;
        var uncompressed = new byte[65];
        uncompressed[0] = 0x04;
        q.X!.CopyTo(uncompressed, 1);
        q.Y!.CopyTo(uncompressed, 33);
        var auth = RandomNumberGenerator.GetBytes(16);
        var payload = Encoding.UTF8.GetBytes("same message twice");

        var first = WebPushEncryptor.Encrypt(uncompressed, auth, payload);
        var second = WebPushEncryptor.Encrypt(uncompressed, auth, payload);

        Assert.NotEqual(first, second);
        Assert.NotEqual(first[..16], second[..16]);
    }
}
