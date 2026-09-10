using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotNexus.Gateway.Notifications.Push;

namespace BotNexus.Gateway.Tests.Notifications.Push;

/// <summary>
/// Pins the bearer token the gateway signs for APNs.
/// </summary>
/// <remarks>
/// The failure mode this guards is opaque: Apple answers a malformed token with
/// InvalidProviderToken and says nothing about which part was wrong, so a signature in the wrong
/// encoding or a missing claim looks exactly like a wrong key. Every field is therefore checked
/// here, and the signature is verified with the public half rather than merely produced.
/// <para>
/// Caching is checked too, from both ends. Apple rejects a token older than an hour AND treats
/// minting one more often than every twenty minutes as abuse - so signing per request, which is
/// the obvious implementation, is the wrong one.
/// </para>
/// </remarks>
public sealed class ApnsTokenProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "botnexus-apns-jwt", Guid.NewGuid().ToString("N"));

    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.Zero));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private string KeyPath => Path.Combine(_dir, "AuthKey.p8");

    public ApnsTokenProviderTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(KeyPath, new string(PemEncoding.Write("PRIVATE KEY", _key.ExportPkcs8PrivateKey())));
    }

    public void Dispose()
    {
        _key.Dispose();
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    private ApnsTokenProvider Provider() => new(
        new ApnsOptions
        {
            TeamId = "TEAM123456",
            KeyId = "KEY1234567",
            BundleId = "com.example.botnexus",
            PrivateKeyPath = KeyPath,
        },
        _time);

    private static JsonElement Segment(string token, int index) =>
        JsonDocument.Parse(Base64Url.Decode(token.Split('.')[index])).RootElement;

    [Fact]
    public void The_header_names_the_algorithm_and_the_key()
    {
        var header = Segment(Provider().GetToken(), 0);

        Assert.Equal("ES256", header.GetProperty("alg").GetString());
        Assert.Equal("KEY1234567", header.GetProperty("kid").GetString());
    }

    [Fact]
    public void The_claims_name_the_team_and_when_it_was_issued()
    {
        var claims = Segment(Provider().GetToken(), 1);

        Assert.Equal("TEAM123456", claims.GetProperty("iss").GetString());
        Assert.Equal(_time.GetUtcNow().ToUnixTimeSeconds(), claims.GetProperty("iat").GetInt64());
    }

    // The load-bearing one. A signature in the wrong encoding is indistinguishable from a wrong
    // key as far as Apple's error is concerned.
    [Fact]
    public void The_signature_verifies_against_the_public_key()
    {
        var token = Provider().GetToken();
        var parts = token.Split('.');
        var signature = Base64Url.Decode(parts[2]);

        // JWS uses the raw r||s pair. SignData defaults to DER, which is longer and variable
        // length - so the size is itself the check that the right format was asked for.
        Assert.Equal(64, signature.Length);

        Assert.True(
            _key.VerifyData(
                Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
            "APNs will reject a token whose signature does not verify, saying only InvalidProviderToken.");
    }

    // Apple treats minting more often than every twenty minutes as abuse.
    [Fact]
    public void Reuses_the_token_rather_than_signing_per_request()
    {
        var provider = Provider();

        var first = provider.GetToken();
        _time.Advance(TimeSpan.FromMinutes(19));

        Assert.Equal(first, provider.GetToken());
    }

    // And rejects one older than an hour, so it cannot simply be cached forever.
    [Fact]
    public void Refreshes_the_token_before_apple_would_reject_it()
    {
        var provider = Provider();

        var first = provider.GetToken();
        _time.Advance(TimeSpan.FromMinutes(46));
        var second = provider.GetToken();

        Assert.NotEqual(first, second);
        Assert.Equal(
            _time.GetUtcNow().ToUnixTimeSeconds(),
            Segment(second, 1).GetProperty("iat").GetInt64());
    }

    // Both bounds together: the refresh window has to sit strictly inside 20 and 60 minutes, and
    // an off-by-one either way is only discoverable in production.
    [Fact]
    public void The_refresh_window_sits_inside_both_of_apples_limits()
    {
        var provider = Provider();
        var first = provider.GetToken();

        _time.Advance(TimeSpan.FromMinutes(21));
        Assert.Equal(first, provider.GetToken());

        _time.Advance(TimeSpan.FromMinutes(38));
        Assert.NotEqual(first, provider.GetToken());
    }
}
