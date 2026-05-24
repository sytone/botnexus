using System.Text.Json;
using BotNexus.Domain.Primitives;
using Vogen;

namespace BotNexus.Domain.Tests;

public sealed class RunIdTests
{
    [Fact]
    public void Create_GeneratesUnique32CharacterGuid()
    {
        var a = RunId.Create();
        var b = RunId.Create();

        a.Value.Length.ShouldBe(32);
        a.ShouldNotBe(b);
    }

    [Fact]
    public void From_TrimsLeadingAndTrailingWhitespace()
    {
        var result = RunId.From(" run-1 ");

        result.Value.ShouldBe("run-1");
    }

    [Fact]
    public void From_RejectsNull()
    {
        Action act = () => RunId.From(null!);

        act.ShouldThrow<ValueObjectValidationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("   \n   ")]
    public void From_RejectsEmptyOrWhitespace(string value)
    {
        Action act = () => RunId.From(value);

        var ex = act.ShouldThrow<ValueObjectValidationException>();
        ex.Message.ShouldContain("RunId");
    }

    [Fact]
    public void Equality_MatchesByValue()
    {
        RunId.From("run-1").ShouldBe(RunId.From("run-1"));
        RunId.From("run-1").ShouldNotBe(RunId.From("run-2"));
    }

    [Fact]
    public void ToString_ReturnsRawValue()
    {
        RunId.From("run-1").ToString().ShouldBe("run-1");
    }

    [Fact]
    public void Json_SerializesAsBareString()
    {
        var json = JsonSerializer.Serialize(RunId.From("run-1"));

        json.ShouldBe("\"run-1\"");
    }

    [Fact]
    public void Json_DeserializesFromBareString()
    {
        var id = JsonSerializer.Deserialize<RunId>("\"run-1\"");

        id.ShouldBe(RunId.From("run-1"));
    }

    [Fact]
    public void Json_RoundTripPreservesValue()
    {
        var original = RunId.From("run-1");

        var roundTrip = JsonSerializer.Deserialize<RunId>(JsonSerializer.Serialize(original));

        roundTrip.ShouldBe(original);
    }

    [Fact]
    public void Json_PropertyOnDtoUsesBareStringWithCamelCase()
    {
        var dto = new { run = RunId.From("run-1"), label = "in-progress" };

        var json = JsonSerializer.Serialize(
            dto,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        json.ShouldBe("{\"run\":\"run-1\",\"label\":\"in-progress\"}");
    }
}
