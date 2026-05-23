using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;

namespace BotNexus.Domain.Tests;

public sealed class CitizenIdTests
{
    [Fact]
    public void Of_UserId_ProducesUserKind()
    {
        var citizen = CitizenId.Of(UserId.From("alice"));

        citizen.Kind.ShouldBe(CitizenKind.User);
        citizen.AsUser.ShouldNotBeNull();
        citizen.AsUser!.Value.Value.ShouldBe("alice");
        citizen.AsAgent.ShouldBeNull();
    }

    [Fact]
    public void Of_AgentId_ProducesAgentKind()
    {
        var citizen = CitizenId.Of(AgentId.From("coding-agent"));

        citizen.Kind.ShouldBe(CitizenKind.Agent);
        citizen.AsAgent.ShouldNotBeNull();
        citizen.AsAgent!.Value.Value.ShouldBe("coding-agent");
        citizen.AsUser.ShouldBeNull();
    }

    [Fact]
    public void Match_DispatchesOnKind()
    {
        var user = CitizenId.Of(UserId.From("alice"));
        var agent = CitizenId.Of(AgentId.From("bob"));

        user.Match(u => $"u:{u.Value}", a => $"a:{a.Value}").ShouldBe("u:alice");
        agent.Match(u => $"u:{u.Value}", a => $"a:{a.Value}").ShouldBe("a:bob");
    }

    [Fact]
    public void Match_OnDefault_Throws()
    {
        var uninitialized = default(CitizenId);

        Action act = () => uninitialized.Match(u => 1, a => 2);

        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact]
    public void Default_IsUnknownAndInvalid()
    {
        var d = default(CitizenId);

        d.Kind.ShouldBe(CitizenKind.Unknown);
        d.IsValid.ShouldBeFalse();
        d.AsUser.ShouldBeNull();
        d.AsAgent.ShouldBeNull();
    }

    [Fact]
    public void Value_OnDefault_Throws()
    {
        var d = default(CitizenId);

        Action act = () => _ = d.Value;

        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact]
    public void ToString_PrefixesByKind()
    {
        CitizenId.Of(UserId.From("alice")).ToString().ShouldBe("user:alice");
        CitizenId.Of(AgentId.From("bob")).ToString().ShouldBe("agent:bob");
    }

    [Fact]
    public void Equality_SameKindAndId_AreEqual()
    {
        var a = CitizenId.Of(UserId.From("alice"));
        var b = CitizenId.Of(UserId.From("alice"));

        (a == b).ShouldBeTrue();
        a.Equals(b).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentKindSameInner_AreNotEqual()
    {
        var asUser = CitizenId.Of(UserId.From("shared-name"));
        var asAgent = CitizenId.Of(AgentId.From("shared-name"));

        (asUser == asAgent).ShouldBeFalse();
        (asUser != asAgent).ShouldBeTrue();
    }

    [Fact]
    public void Equality_TwoDefaults_AreEqual()
    {
        (default(CitizenId) == default(CitizenId)).ShouldBeTrue();
    }

    [Fact]
    public void Json_WritesKindAndId_AsObject()
    {
        var json = JsonSerializer.Serialize(CitizenId.Of(UserId.From("alice")));

        json.ShouldBe("{\"kind\":\"User\",\"id\":\"alice\"}");
    }

    [Fact]
    public void Json_ReadsCanonicalShape()
    {
        var citizen = JsonSerializer.Deserialize<CitizenId>("{\"kind\":\"Agent\",\"id\":\"coding-agent\"}");

        citizen.Kind.ShouldBe(CitizenKind.Agent);
        citizen.AsAgent!.Value.Value.ShouldBe("coding-agent");
    }

    [Theory]
    [InlineData("{\"Kind\":\"User\",\"Id\":\"alice\"}")]
    [InlineData("{\"kind\":\"user\",\"id\":\"alice\"}")]
    [InlineData("{\"KIND\":\"USER\",\"ID\":\"alice\"}")]
    public void Json_PropertyNamesAreCaseInsensitive(string json)
    {
        var citizen = JsonSerializer.Deserialize<CitizenId>(json);

        citizen.Kind.ShouldBe(CitizenKind.User);
        citizen.AsUser!.Value.Value.ShouldBe("alice");
    }

    [Theory]
    [InlineData("{\"kind\":1,\"id\":\"alice\"}", CitizenKind.User)]
    [InlineData("{\"kind\":2,\"id\":\"agent-1\"}", CitizenKind.Agent)]
    public void Json_KindAcceptsNumericValues(string json, CitizenKind expected)
    {
        var citizen = JsonSerializer.Deserialize<CitizenId>(json);

        citizen.Kind.ShouldBe(expected);
    }

    [Fact]
    public void Json_RoundTripPreservesValue()
    {
        var original = CitizenId.Of(AgentId.From("agent-x"));

        var roundTrip = JsonSerializer.Deserialize<CitizenId>(JsonSerializer.Serialize(original));

        roundTrip.ShouldBe(original);
    }

    [Fact]
    public void Json_MissingKind_Throws()
    {
        Action act = () => JsonSerializer.Deserialize<CitizenId>("{\"id\":\"alice\"}");

        act.ShouldThrow<JsonException>();
    }

    [Fact]
    public void Json_MissingId_Throws()
    {
        Action act = () => JsonSerializer.Deserialize<CitizenId>("{\"kind\":\"User\"}");

        act.ShouldThrow<JsonException>();
    }

    [Fact]
    public void Json_UnknownKind_Throws()
    {
        Action act = () => JsonSerializer.Deserialize<CitizenId>("{\"kind\":\"Cat\",\"id\":\"whiskers\"}");

        act.ShouldThrow<JsonException>();
    }
}
