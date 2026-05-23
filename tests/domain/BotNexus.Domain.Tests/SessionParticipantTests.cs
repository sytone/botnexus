using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;

namespace BotNexus.Domain.Tests;

public sealed class SessionParticipantTests
{
    [Fact]
    public void Json_NewShape_RoundTrips()
    {
        var participant = new SessionParticipant
        {
            CitizenId = CitizenId.Of(UserId.From("alice")),
            Role = "caller",
        };

        var json = JsonSerializer.Serialize(participant);
        var roundTrip = JsonSerializer.Deserialize<SessionParticipant>(json);

        roundTrip.ShouldNotBeNull();
        roundTrip!.CitizenId.ShouldBe(participant.CitizenId);
        roundTrip.Role.ShouldBe("caller");
    }

    [Fact]
    public void Json_WritesBothNewAndLegacyShape_ForRollbackSafety()
    {
        var participant = new SessionParticipant
        {
            CitizenId = CitizenId.Of(AgentId.From("agent-1")),
            Role = "target",
        };

        var json = JsonSerializer.Serialize(participant);

        json.ShouldContain("\"citizenId\":");
        json.ShouldContain("\"type\":\"Agent\"");
        json.ShouldContain("\"id\":\"agent-1\"");
        json.ShouldContain("\"role\":\"target\"");
    }

    [Theory]
    [InlineData("{\"type\":\"User\",\"id\":\"alice\"}", CitizenKind.User, "alice")]
    [InlineData("{\"type\":\"Agent\",\"id\":\"bot\"}", CitizenKind.Agent, "bot")]
    [InlineData("{\"type\":0,\"id\":\"alice\"}", CitizenKind.User, "alice")]
    [InlineData("{\"type\":1,\"id\":\"bot\"}", CitizenKind.Agent, "bot")]
    public void Json_ReadsLegacyShape(string legacyJson, CitizenKind expectedKind, string expectedId)
    {
        var participant = JsonSerializer.Deserialize<SessionParticipant>(legacyJson);

        participant.ShouldNotBeNull();
        participant!.CitizenId.Kind.ShouldBe(expectedKind);
        participant.CitizenId.Value.ShouldBe(expectedId);
    }

    [Fact]
    public void Json_LegacyShape_PreservesRole()
    {
        var participant = JsonSerializer.Deserialize<SessionParticipant>(
            "{\"type\":\"Agent\",\"id\":\"agent-1\",\"role\":\"target\"}");

        participant.ShouldNotBeNull();
        participant!.CitizenId.Kind.ShouldBe(CitizenKind.Agent);
        participant.Role.ShouldBe("target");
    }

    [Fact]
    public void Json_LegacyShape_IgnoresWorldId()
    {
        var participant = JsonSerializer.Deserialize<SessionParticipant>(
            "{\"type\":\"Agent\",\"id\":\"agent-1\",\"worldId\":\"world-a\",\"role\":\"initiator\"}");

        participant.ShouldNotBeNull();
        participant!.CitizenId.Kind.ShouldBe(CitizenKind.Agent);
        participant.CitizenId.Value.ShouldBe("agent-1");
        participant.Role.ShouldBe("initiator");
    }

    [Fact]
    public void Json_PropertyNamesAreCaseInsensitive()
    {
        var participant = JsonSerializer.Deserialize<SessionParticipant>(
            "{\"Type\":\"User\",\"ID\":\"alice\",\"ROLE\":\"caller\"}");

        participant.ShouldNotBeNull();
        participant!.CitizenId.Kind.ShouldBe(CitizenKind.User);
        participant.CitizenId.Value.ShouldBe("alice");
        participant.Role.ShouldBe("caller");
    }

    [Fact]
    public void Json_MissingBothShapes_Throws()
    {
        Action act = () => JsonSerializer.Deserialize<SessionParticipant>("{\"role\":\"caller\"}");

        act.ShouldThrow<JsonException>();
    }

    [Fact]
    public void Json_BothShapesPresentAndAgree_DeserializesSuccessfully()
    {
        var both = "{\"citizenId\":{\"kind\":\"Agent\",\"id\":\"agent-1\"},\"type\":\"Agent\",\"id\":\"agent-1\"}";

        var participant = JsonSerializer.Deserialize<SessionParticipant>(both);

        participant.ShouldNotBeNull();
        participant!.CitizenId.Kind.ShouldBe(CitizenKind.Agent);
        participant.CitizenId.Value.ShouldBe("agent-1");
    }

    [Fact]
    public void Json_BothShapesPresentAndDisagree_Throws()
    {
        var conflicting = "{\"citizenId\":{\"kind\":\"User\",\"id\":\"alice\"},\"type\":\"Agent\",\"id\":\"agent-1\"}";

        Action act = () => JsonSerializer.Deserialize<SessionParticipant>(conflicting);

        act.ShouldThrow<JsonException>();
    }

    [Fact]
    public void Json_UnknownFieldsAreTolerated()
    {
        var withExtras = "{\"type\":\"User\",\"id\":\"alice\",\"capabilityLevel\":3,\"tags\":[\"vip\"]}";

        var participant = JsonSerializer.Deserialize<SessionParticipant>(withExtras);

        participant.ShouldNotBeNull();
        participant!.CitizenId.Value.ShouldBe("alice");
    }

    [Fact]
    public void Json_ListOfMixedShapes_DeserializesAll()
    {
        var json = "[" +
                   "{\"type\":\"User\",\"id\":\"alice\"}," +
                   "{\"citizenId\":{\"kind\":\"Agent\",\"id\":\"bot\"},\"role\":\"target\"}" +
                   "]";

        var participants = JsonSerializer.Deserialize<List<SessionParticipant>>(json);

        participants.ShouldNotBeNull();
        participants!.Count.ShouldBe(2);
        participants[0].CitizenId.Kind.ShouldBe(CitizenKind.User);
        participants[1].CitizenId.Kind.ShouldBe(CitizenKind.Agent);
        participants[1].Role.ShouldBe("target");
    }
}
