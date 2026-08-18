using System.Reflection;
using BotNexus.Extensions.Channels.Matrix.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.Matrix.Tests;

/// <summary>
/// Guards the credential-containment property behind CodeQL alert 110
/// (<c>cs/cleartext-storage-of-sensitive-information</c>): the Matrix access token must reach the
/// client factory and nothing else. It must not be retained on the long-lived
/// <see cref="MatrixAccountRuntime"/> record, from which a future log statement, serialiser or
/// crash-dump walk could surface it.
/// </summary>
/// <remarks>
/// These assertions are structural, not textual — they inspect the runtime record's actual
/// properties by reflection rather than grepping source, so the property survives a rename and
/// fails if anyone re-adds a credential-carrying member.
/// </remarks>
public sealed class MatrixCredentialContainmentTests
{
    private const string Token = "syt_super_secret_token_value";

    private static MatrixChannelOptions BuildOptions()
    {
        var options = new MatrixChannelOptions { Homeserver = "https://matrix.example.com" };
        options.Agents["farnsworth"] = new MatrixAccountConfig
        {
            UserId = "@farnsworth:example.com",
            AccessToken = Token,
            AgentId = "farnsworth",
            AllowedRoomIds = { "!allowed:example.com" },
            AllowedUserIds = { "@jon:example.com" },
        };
        return options;
    }

    private static MatrixChannelAdapter CreateAdapter(FakeMatrixClientFactory factory) =>
        new(
            NullLogger<MatrixChannelAdapter>.Instance,
            new OptionsWrapper<MatrixChannelOptions>(BuildOptions()),
            factory);

    [Fact]
    public void AccountRuntime_DoesNotExposeTheRawConfiguration()
    {
        // Holding MatrixAccountConfig would transitively expose AccessToken, which is exactly the
        // storage CodeQL flagged. The runtime must hold the token-free projection instead.
        var properties = typeof(MatrixAccountRuntime)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        properties.ShouldNotContain(p => p.PropertyType == typeof(MatrixAccountConfig));
        properties.ShouldContain(p => p.PropertyType == typeof(MatrixAccountIdentity));
    }

    [Fact]
    public void AccountIdentity_HasNoAccessTokenMember()
    {
        var members = typeof(MatrixAccountIdentity)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(m => m.Name);

        members.ShouldNotContain(n => n.Contains("Token", StringComparison.OrdinalIgnoreCase));
        members.ShouldNotContain(n => n.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        members.ShouldNotContain(n => n.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MaterialisedRuntime_HasNoStringPropertyEqualToTheAccessToken()
    {
        // The load-bearing behavioural assertion: walk every string-valued property actually
        // present on a live runtime record and prove none of them is the token.
        var factory = new FakeMatrixClientFactory();
        var adapter = CreateAdapter(factory);

        var runtime = adapter.GetAccount("farnsworth");
        runtime.ShouldNotBeNull();

        var stringValues = typeof(MatrixAccountRuntime)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => (string?)p.GetValue(runtime));

        stringValues.ShouldNotContain(Token);
    }

    [Fact]
    public void AccountIdentity_RenderedForm_DoesNotContainTheAccessToken()
    {
        // A record's compiler-generated ToString prints every member, so it is the most likely
        // accidental leak path (an interpolated log line, a debugger view). Proving the token is
        // absent from the rendered form proves it is absent from the members.
        var identity = MatrixAccountIdentity.FromConfig(BuildOptions().Agents["farnsworth"]);

        identity.ToString().ShouldNotContain(Token);
        identity.ToString().ShouldContain("@farnsworth:example.com");
    }

    [Fact]
    public void TokenStillReachesTheClientFactory()
    {
        // Anti-vacuity: the containment assertions above would also pass if the token were never
        // used at all, which would be a broken adapter rather than a secure one. The token must
        // still be delivered exactly where it is needed.
        var factory = new FakeMatrixClientFactory();
        CreateAdapter(factory).GetAccountCount().ShouldBe(1);

        factory.Credentials["farnsworth"].AccessToken.Reveal().ShouldBe(Token);
    }

    [Fact]
    public void AccessToken_ToStringIsRedacted()
    {
        // The single most likely accidental-disclosure path: interpolation into a log message, an
        // exception, or a {Token} structured-logging placeholder. All of those route through
        // ToString, so a redacting ToString is what makes those leaks impossible rather than merely
        // unobserved.
        MatrixAccessToken.TryCreate(Token, out var token).ShouldBeTrue();

        token.ToString().ShouldBe(MatrixAccessToken.RedactedMarker);
        token.ToString().ShouldNotContain(Token);
        $"token={token}".ShouldNotContain(Token);
    }

    [Fact]
    public void AccessToken_RevealReturnsTheRawValue()
    {
        // Anti-vacuity for the redaction tests: a wrapper that could not return the credential at
        // all would satisfy every "does not contain" assertion while breaking authentication.
        MatrixAccessToken.TryCreate(Token, out var token).ShouldBeTrue();

        token.HasValue.ShouldBeTrue();
        token.Reveal().ShouldBe(Token);
        token.Length.ShouldBe(Token.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AccessToken_AbsentValueIsRepresentableAndRejected(string? raw)
    {
        // An unset token must be distinguishable from a valid one rather than becoming an empty
        // string that silently authenticates as nobody.
        MatrixAccessToken.TryCreate(raw, out var token).ShouldBeFalse();

        token.HasValue.ShouldBeFalse();
        token.Length.ShouldBe(0);
        Should.Throw<InvalidOperationException>(() => token.Reveal());
    }

    [Fact]
    public void AccountWithNoAccessToken_IsSkippedAndTheKeyIsStillLogged()
    {
        // The completeness check now reads HasValue rather than the raw string. An incomplete
        // account must still be skipped rather than started with an absent credential.
        var options = new MatrixChannelOptions { Homeserver = "https://matrix.example.com" };
        options.Agents["broken"] = new MatrixAccountConfig { UserId = "@a:example.com", AccessToken = null };

        var adapter = new MatrixChannelAdapter(
            NullLogger<MatrixChannelAdapter>.Instance,
            new OptionsWrapper<MatrixChannelOptions>(options),
            new FakeMatrixClientFactory());

        adapter.GetAccountCount().ShouldBe(0);
    }

    [Fact]
    public void ClientFactoryContract_TakesTheWrapperNotABareString()
    {
        // Pins the seam itself. If the factory ever goes back to a bare string parameter, the raw
        // credential becomes flowable into logs again and CodeQL's taint path reopens.
        var parameter = typeof(IMatrixClientFactory)
            .GetMethod(nameof(IMatrixClientFactory.Create))!
            .GetParameters()
            .Single(p => p.Name == "accessToken");

        parameter.ParameterType.ShouldBe(typeof(MatrixAccessToken));
    }

    [Fact]
    public void Identity_PreservesEveryAuthorizationFactTheAdapterNeeds()
    {
        // The projection must not silently drop an allow-list: doing so would WIDEN authorization
        // (an empty allow-list permits everything), turning a credential fix into an access-control
        // regression.
        var identity = MatrixAccountIdentity.FromConfig(BuildOptions().Agents["farnsworth"]);

        identity.UserId.ShouldBe("@farnsworth:example.com");
        identity.AutoJoin.ShouldBeTrue();
        identity.IsRoomAllowed("!allowed:example.com").ShouldBeTrue();
        identity.IsRoomAllowed("!other:example.com").ShouldBeFalse();
        identity.IsUserAllowed("@jon:example.com").ShouldBeTrue();
        identity.IsUserAllowed("@stranger:example.com").ShouldBeFalse();
    }

    [Fact]
    public void Identity_CopiesAllowListsSoLaterConfigMutationCannotWidenAuthorization()
    {
        var config = BuildOptions().Agents["farnsworth"];
        var identity = MatrixAccountIdentity.FromConfig(config);

        config.AllowedRoomIds.Add("!sneaked-in:example.com");

        identity.IsRoomAllowed("!sneaked-in:example.com").ShouldBeFalse();
    }

    [Fact]
    public void Identity_EmptyAllowListsPermitEverything()
    {
        var config = new MatrixAccountConfig { UserId = "@a:example.com", AccessToken = Token };
        var identity = MatrixAccountIdentity.FromConfig(config);

        identity.IsRoomAllowed("!anything:example.com").ShouldBeTrue();
        identity.IsUserAllowed("@anyone:example.com").ShouldBeTrue();
    }
}
