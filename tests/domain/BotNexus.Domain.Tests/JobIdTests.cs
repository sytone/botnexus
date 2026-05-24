using System.Text.Json;
using BotNexus.Domain.Primitives;
using Vogen;

namespace BotNexus.Domain.Tests;

public sealed class JobIdTests
{
    [Fact]
    public void From_TrimsLeadingAndTrailingWhitespace()
    {
        var result = JobId.From(" job-1 ");

        result.Value.ShouldBe("job-1");
    }

    [Fact]
    public void From_RejectsNull()
    {
        Action act = () => JobId.From(null!);

        act.ShouldThrow<ValueObjectValidationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("   \n   ")]
    public void From_RejectsEmptyOrWhitespace(string value)
    {
        Action act = () => JobId.From(value);

        var ex = act.ShouldThrow<ValueObjectValidationException>();
        ex.Message.ShouldContain("JobId");
    }

    [Fact]
    public void Equality_MatchesByValue()
    {
        JobId.From("job-1").ShouldBe(JobId.From("job-1"));
        JobId.From("job-1").ShouldNotBe(JobId.From("job-2"));
    }

    [Fact]
    public void ToString_ReturnsRawValue()
    {
        JobId.From("job-1").ToString().ShouldBe("job-1");
    }

    [Fact]
    public void Json_SerializesAsBareString()
    {
        var json = JsonSerializer.Serialize(JobId.From("job-1"));

        json.ShouldBe("\"job-1\"");
    }

    [Fact]
    public void Json_DeserializesFromBareString()
    {
        var id = JsonSerializer.Deserialize<JobId>("\"job-1\"");

        id.ShouldBe(JobId.From("job-1"));
    }

    [Fact]
    public void Json_RoundTripPreservesValue()
    {
        var original = JobId.From("job-1");

        var roundTrip = JsonSerializer.Deserialize<JobId>(JsonSerializer.Serialize(original));

        roundTrip.ShouldBe(original);
    }

    [Fact]
    public void Json_PropertyOnDtoUsesBareStringWithCamelCase()
    {
        var dto = new { job = JobId.From("job-1"), label = "scheduled" };

        var json = JsonSerializer.Serialize(
            dto,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        json.ShouldBe("{\"job\":\"job-1\",\"label\":\"scheduled\"}");
    }
}
