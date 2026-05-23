using System.Reflection;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using Vogen;

namespace BotNexus.Domain.Tests;

public sealed class ConversationIdTests
{
    [Fact]
    public void From_TrimsLeadingAndTrailingWhitespace()
    {
        var result = ConversationId.From(" conversation-1 ");

        result.Value.ShouldBe("conversation-1");
    }

    [Fact]
    public void From_RejectsNull()
    {
        Action act = () => ConversationId.From(null!);

        act.ShouldThrow<ValueObjectValidationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("   \n   ")]
    public void From_RejectsEmptyOrWhitespace(string value)
    {
        Action act = () => ConversationId.From(value);

        var ex = act.ShouldThrow<ValueObjectValidationException>();
        ex.Message.ShouldContain("ConversationId");
    }

    [Fact]
    public void Equality_MatchesByValue()
    {
        ConversationId.From("conversation-1").ShouldBe(ConversationId.From("conversation-1"));
        ConversationId.From("conversation-1").ShouldNotBe(ConversationId.From("conversation-2"));
    }

    [Fact]
    public void ToString_ReturnsRawValue()
    {
        ConversationId.From("conversation-1").ToString().ShouldBe("conversation-1");
    }

    [Fact]
    public void Create_GeneratesValueWithCPrefix()
    {
        var id = ConversationId.Create();

        id.Value.ShouldStartWith("c_");
        id.Value.Length.ShouldBe("c_".Length + 32);
    }

    [Fact]
    public void Create_GeneratesDistinctValues()
    {
        ConversationId.Create().ShouldNotBe(ConversationId.Create());
    }

    [Fact]
    public void Json_SerializesAsBareString()
    {
        var json = JsonSerializer.Serialize(ConversationId.From("conversation-1"));

        json.ShouldBe("\"conversation-1\"");
    }

    [Fact]
    public void Json_DeserializesFromBareString()
    {
        var id = JsonSerializer.Deserialize<ConversationId>("\"conversation-1\"");

        id.ShouldBe(ConversationId.From("conversation-1"));
    }

    [Fact]
    public void Json_RoundTripPreservesValue()
    {
        var original = ConversationId.From("conversation-1");

        var roundTrip = JsonSerializer.Deserialize<ConversationId>(JsonSerializer.Serialize(original));

        roundTrip.ShouldBe(original);
    }

    [Fact]
    public void Json_PropertyOnDtoUsesBareStringWithCamelCase()
    {
        var dto = new { conversation = ConversationId.From("conversation-1"), label = "hello" };

        var json = JsonSerializer.Serialize(
            dto,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        json.ShouldBe("{\"conversation\":\"conversation-1\",\"label\":\"hello\"}");
    }

    [Fact]
    public void Type_HasNoImplicitStringOperator()
    {
        var implicitOperators = typeof(ConversationId)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "op_Implicit")
            .ToArray();

        implicitOperators.ShouldBeEmpty(
            "ConversationId must not expose implicit conversions to/from string. " +
            "Use .Value and .From() explicitly.");
    }
}
