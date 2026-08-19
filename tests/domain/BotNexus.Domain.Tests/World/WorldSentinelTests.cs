using BotNexus.Domain.World;
using Shouldly;

namespace BotNexus.Domain.Tests.World;

/// <summary>
/// Behaviour pins for the shared world-sentinel decision (#2836).
/// </summary>
/// <remarks>
/// These cover the <b>comparison</b>, deliberately with no IO. The comparison is the single piece
/// three consumers share, so it is the piece that must not have two spellings - the same
/// "one value, one derivation" discipline the SQLite guard (#2833) was built on.
/// </remarks>
public sealed class WorldSentinelTests
{
    private const string WorldA = "11111111-1111-1111-1111-111111111111";
    private const string WorldB = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void Serialize_RoundTripsThroughParse()
    {
        var parsed = WorldSentinel.Parse(WorldSentinel.Serialize(WorldA, "1.2.3.4"));

        parsed.ShouldNotBeNull();
        parsed!.WorldId.ShouldBe(WorldA);
        parsed.CreatedByVersion.ShouldBe("1.2.3.4");
        DateTimeOffset.TryParse(parsed.CreatedAt, out _).ShouldBeTrue(
            "created_at exists for forensics; an unparseable timestamp makes it useless.");
    }

    [Fact]
    public void Serialize_UsesTheSameKeysAsTheSqliteStamp()
    {
        var json = WorldSentinel.Serialize(WorldA, "1.0.0.0");

        json.ShouldContain("\"world_id\"");
        json.ShouldContain("\"created_at\"");
        json.ShouldContain("\"created_by_version\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("nonsense")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("{}")]
    [InlineData("{\"world_id\":null}")]
    [InlineData("{\"world_id\":42}")]
    [InlineData("{\"world_id\":\"  \"}")]
    public void Parse_ReturnsNullForAnythingWithoutAUsableWorldId(string? contents)
        => WorldSentinel.Parse(contents).ShouldBeNull();

    [Fact]
    public void Classify_MatchingIdIsAMatch()
        => WorldSentinel.Classify(WorldA, new WorldSentinelDocument(WorldA, null, null), homeIsPopulated: true)
            .ShouldBe(WorldSentinelVerdict.Match);

    [Fact]
    public void Classify_DifferingIdIsAMismatch()
        => WorldSentinel.Classify(WorldA, new WorldSentinelDocument(WorldB, null, null), homeIsPopulated: true)
            .ShouldBe(WorldSentinelVerdict.Mismatch);

    [Fact]
    public void Classify_IgnoresCaseBecauseGuidFormattingIsNotIdentity()
        => WorldSentinel.Classify(WorldA, new WorldSentinelDocument(WorldA.ToUpperInvariant(), null, null), true)
            .ShouldBe(WorldSentinelVerdict.Match);

    [Fact]
    public void Classify_NoSentinelOnPopulatedHomeIsAnAdoption()
        => WorldSentinel.Classify(WorldA, sentinel: null, homeIsPopulated: true)
            .ShouldBe(WorldSentinelVerdict.Adopt);

    [Fact]
    public void Classify_NoSentinelOnEmptyHomeIsASilentStamp()
        => WorldSentinel.Classify(WorldA, sentinel: null, homeIsPopulated: false)
            .ShouldBe(WorldSentinelVerdict.Stamp);

    [Fact]
    public void DescribeMismatch_NamesBothWorldsAndThePath()
    {
        var message = WorldSentinel.DescribeMismatch(WorldA, WorldB, "/srv/homes/beta");

        message.ShouldContain(WorldA);
        message.ShouldContain(WorldB);
        message.ShouldContain("/srv/homes/beta");
    }

    [Fact]
    public void MismatchException_CarriesTheStructuredFactsNotJustProse()
    {
        var exception = new HomeWorldIdentityMismatchException(WorldA, WorldB, "/srv/homes/beta");

        exception.ExpectedWorldId.ShouldBe(WorldA);
        exception.ActualWorldId.ShouldBe(WorldB);
        exception.HomePath.ShouldBe("/srv/homes/beta");
    }
}

/// <summary>
/// Behaviour pins for the containment rule that keeps file-backed stores inside the verified home
/// (#2836, AC4).
/// </summary>
public sealed class HomeScopeTests
{
    private sealed record FakeHome(string RootPath, string? WorldId) : IVerifiedHome;

    private static readonly IVerifiedHome Home =
        new FakeHome(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bn-home")), "world-a");

    [Fact]
    public void PathInsideTheHome_IsAccepted()
        => Should.NotThrow(() => HomeScope.EnsureWithin(Home, Path.Combine(Home.RootPath, "sessions")));

    [Fact]
    public void TheHomeItself_IsAccepted()
        => Should.NotThrow(() => HomeScope.EnsureWithin(Home, Home.RootPath));

    [Fact]
    public void PathOutsideTheHome_IsRefusedNamingBothPaths()
    {
        var stray = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "somewhere-else", "sessions"));

        var exception = Should.Throw<HomeScopeViolationException>(() => HomeScope.EnsureWithin(Home, stray));

        exception.StorePath.ShouldBe(stray);
        exception.HomePath.ShouldBe(Home.RootPath);
    }

    [Fact]
    public void SiblingWithASharedPrefix_IsNotInsideTheHome()
    {
        // "bn-home-other" starts with "bn-home". A naive StartsWith would accept it, which is how a
        // containment check silently stops containing anything.
        var sibling = Home.RootPath + "-other";

        Should.Throw<HomeScopeViolationException>(() => HomeScope.EnsureWithin(Home, sibling));
    }

    [Fact]
    public void TraversalOutOfTheHome_IsRefused()
    {
        var escape = Path.Combine(Home.RootPath, "..", "elsewhere");

        Should.Throw<HomeScopeViolationException>(() => HomeScope.EnsureWithin(Home, escape));
    }

    [Fact]
    public void WithoutAHome_TheCheckIsInert()
        => Should.NotThrow(() => HomeScope.EnsureWithin(home: null, storePath: @"C:\anywhere\at\all"));
}
