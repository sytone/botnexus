using System.Text.Json;
using BotNexus.Domain.Primitives;
using Vogen;

namespace BotNexus.Domain.Tests;

public sealed class UserIdTests
{
    [Fact]
    public void From_TrimsLeadingAndTrailingWhitespace()
    {
        var result = UserId.From(" alice ");

        result.Value.ShouldBe("alice");
    }

    [Fact]
    public void From_RejectsNull()
    {
        Action act = () => UserId.From(null!);

        act.ShouldThrow<ValueObjectValidationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("   \n   ")]
    public void From_RejectsEmptyOrWhitespace(string value)
    {
        Action act = () => UserId.From(value);

        var ex = act.ShouldThrow<ValueObjectValidationException>();
        ex.Message.ShouldContain("UserId");
    }

    [Fact]
    public void Equality_MatchesByValue()
    {
        UserId.From("alice").ShouldBe(UserId.From("alice"));
        UserId.From("alice").ShouldNotBe(UserId.From("bob"));
    }

    [Fact]
    public void ToString_ReturnsRawValue()
    {
        UserId.From("alice").ToString().ShouldBe("alice");
    }

    [Fact]
    public void Json_SerializesAsBareString()
    {
        var json = JsonSerializer.Serialize(UserId.From("alice"));

        json.ShouldBe("\"alice\"");
    }

    [Fact]
    public void Json_DeserializesFromBareString()
    {
        var id = JsonSerializer.Deserialize<UserId>("\"alice\"");

        id.ShouldBe(UserId.From("alice"));
    }

    [Fact]
    public void Json_RoundTripPreservesValue()
    {
        var original = UserId.From("alice");

        var roundTrip = JsonSerializer.Deserialize<UserId>(JsonSerializer.Serialize(original));

        roundTrip.ShouldBe(original);
    }

    [Fact]
    public void Json_PropertyOnDtoUsesBareStringWithCamelCase()
    {
        var dto = new { user = UserId.From("alice"), label = "hello" };

        var json = JsonSerializer.Serialize(
            dto,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        json.ShouldBe("{\"user\":\"alice\",\"label\":\"hello\"}");
    }
}
