using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Pins the auth-profile identity used to scope provider suspensions (#3015).
/// </summary>
/// <remarks>
/// The identifier is the auth-profile half of a suspension key. Two properties matter and are
/// asserted separately, because getting either wrong is silent:
/// <list type="bullet">
/// <item><b>Stability</b> -- the same credential must yield the same id, or a suspension recorded on
/// one turn would be invisible on the next and the whole "remember the condition" premise of #3015
/// collapses back to the per-turn amnesia it was filed to fix.</item>
/// <item><b>Secret safety</b> -- the id is a dictionary key that may be logged, so it must never
/// contain or equal the credential. A test that only checked "different keys differ" would pass
/// happily on an implementation that returned the raw key.</item>
/// </list>
/// </remarks>
public sealed class GatewayAuthProfileIdTests
{
    /// <summary>The same credential always resolves to the same profile id.</summary>
    [Fact]
    public void DeriveAuthProfileId_IsStableForTheSameCredential()
    {
        var first = GatewayAuthManager.DeriveAuthProfileId("sk-secret-value");
        var second = GatewayAuthManager.DeriveAuthProfileId("sk-secret-value");

        Assert.Equal(first, second);
    }

    /// <summary>
    /// Different credentials resolve to different profile ids -- this is what makes a second auth
    /// profile independent of the first's suspension.
    /// </summary>
    [Fact]
    public void DeriveAuthProfileId_DiffersBetweenCredentials()
    {
        var a = GatewayAuthManager.DeriveAuthProfileId("sk-profile-a");
        var b = GatewayAuthManager.DeriveAuthProfileId("sk-profile-b");

        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// The id must never be, contain, or be contained by the credential. Asserted in both directions
    /// so neither a raw passthrough nor a truncated prefix of the secret can slip through.
    /// </summary>
    [Fact]
    public void DeriveAuthProfileId_NeverLeaksTheCredential()
    {
        const string secret = "sk-live-0123456789abcdefghijklmnop";
        var id = GatewayAuthManager.DeriveAuthProfileId(secret);

        Assert.NotEqual(secret, id);
        Assert.DoesNotContain(secret, id, StringComparison.Ordinal);
        Assert.DoesNotContain(id, secret, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A missing credential yields a single consistent scope rather than a null key, so a provider
    /// using ambient credentials is still scoped rather than unscoped.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DeriveAuthProfileId_NoCredential_ReturnsDefaultScope(string? apiKey)
        => Assert.Equal("default", GatewayAuthManager.DeriveAuthProfileId(apiKey));
}
