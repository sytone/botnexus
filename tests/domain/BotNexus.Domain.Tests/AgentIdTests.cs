using System.Text.Json;
using BotNexus.Domain.Primitives;
using Vogen;

namespace BotNexus.Domain.Tests;

public sealed class AgentIdTests
{
    [Fact]
    public void From_TrimsLeadingAndTrailingWhitespace()
    {
        var result = AgentId.From(" agent-1 ");

        result.Value.ShouldBe("agent-1");
    }

    [Fact]
    public void From_RejectsNull()
    {
        // Vogen throws its built-in "cannot create with null" message for the null path,
        // which does not run through our custom Validate; assert the exception type only.
        Action act = () => AgentId.From(null!);

        act.ShouldThrow<ValueObjectValidationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("   \n   ")]
    public void From_RejectsEmptyOrWhitespace(string value)
    {
        Action act = () => AgentId.From(value);

        var ex = act.ShouldThrow<ValueObjectValidationException>();
        ex.Message.ShouldContain("AgentId");
    }

    [Fact]
    public void Equality_MatchesByValue()
    {
        AgentId.From("agent-1").ShouldBe(AgentId.From("agent-1"));
        AgentId.From("agent-1").ShouldNotBe(AgentId.From("agent-2"));
    }

    [Fact]
    public void ToString_ReturnsRawValue()
    {
        AgentId.From("agent-1").ToString().ShouldBe("agent-1");
    }

    [Fact]
    public void Json_SerializesAsBareString()
    {
        var json = JsonSerializer.Serialize(AgentId.From("agent-1"));

        json.ShouldBe("\"agent-1\"");
    }

    [Fact]
    public void Json_DeserializesFromBareString()
    {
        var id = JsonSerializer.Deserialize<AgentId>("\"agent-1\"");

        id.ShouldBe(AgentId.From("agent-1"));
    }

    [Fact]
    public void Json_RoundTripPreservesValue()
    {
        var original = AgentId.From("agent-1");

        var roundTrip = JsonSerializer.Deserialize<AgentId>(JsonSerializer.Serialize(original));

        roundTrip.ShouldBe(original);
    }

    [Fact]
    public void Json_PropertyOnDtoUsesBareStringWithCamelCase()
    {
        var dto = new { agent = AgentId.From("agent-1"), label = "hello" };

        var json = JsonSerializer.Serialize(
            dto,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        json.ShouldBe("{\"agent\":\"agent-1\",\"label\":\"hello\"}");
    }
}
