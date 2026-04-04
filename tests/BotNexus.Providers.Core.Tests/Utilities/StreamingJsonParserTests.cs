using BotNexus.Providers.Core.Utilities;
using FluentAssertions;

namespace BotNexus.Providers.Core.Tests.Utilities;

public class StreamingJsonParserTests
{
    [Fact]
    public void CompleteJson_ParsedCorrectly()
    {
        var result = StreamingJsonParser.Parse("{\"name\": \"test\", \"value\": 42}");

        result.Should().ContainKey("name").WhoseValue.Should().Be("test");
        result.Should().ContainKey("value");
    }

    [Fact]
    public void PartialJson_UnclosedString_Repaired()
    {
        var result = StreamingJsonParser.Parse("{\"name\": \"partial");

        result.Should().ContainKey("name").WhoseValue.Should().Be("partial");
    }

    [Fact]
    public void PartialJson_UnclosedObject_Repaired()
    {
        var result = StreamingJsonParser.Parse("{\"key\": \"value\"");

        result.Should().ContainKey("key").WhoseValue.Should().Be("value");
    }

    [Fact]
    public void EmptyInput_ReturnsEmptyDict()
    {
        var result = StreamingJsonParser.Parse("");

        result.Should().BeEmpty();
    }

    [Fact]
    public void WhitespaceInput_ReturnsEmptyDict()
    {
        var result = StreamingJsonParser.Parse("   ");

        result.Should().BeEmpty();
    }

    [Fact]
    public void NestedObjects_Parsed()
    {
        var result = StreamingJsonParser.Parse("{\"outer\": {\"inner\": \"value\"}}");

        result.Should().ContainKey("outer");
        var outer = result["outer"] as Dictionary<string, object?>;
        outer.Should().NotBeNull();
        outer!["inner"].Should().Be("value");
    }

    [Fact]
    public void ArrayValues_Parsed()
    {
        var result = StreamingJsonParser.Parse("{\"items\": [1, 2, 3]}");

        result.Should().ContainKey("items");
        var items = result["items"] as List<object?>;
        items.Should().NotBeNull();
        items.Should().HaveCount(3);
    }

    [Fact]
    public void TrailingCommas_Handled()
    {
        var result = StreamingJsonParser.Parse("{\"a\": 1, \"b\": 2,}");

        result.Should().ContainKey("a");
        result.Should().ContainKey("b");
    }
}
