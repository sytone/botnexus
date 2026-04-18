using System.Text.Json;
using BotNexus.Agent.Providers.Core.Utilities;
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
        var items = result["items"];
        items.Should().BeOfType<JsonElement>();
        var element = (JsonElement)items!;
        element.ValueKind.Should().Be(JsonValueKind.Array);
        element.GetArrayLength().Should().Be(3);
    }

    [Fact]
    public void ArrayOfStrings_PreservedAsJsonElement()
    {
        var result = StreamingJsonParser.Parse("{\"command\": [\"echo\", \"hello\"]}");

        result.Should().ContainKey("command");
        var command = result["command"];
        command.Should().BeOfType<JsonElement>();
        var element = (JsonElement)command!;
        element.ValueKind.Should().Be(JsonValueKind.Array);
        element.GetArrayLength().Should().Be(2);
        var items = element.EnumerateArray().ToList();
        items[0].GetString().Should().Be("echo");
        items[1].GetString().Should().Be("hello");
    }

    [Fact]
    public void ArrayOfObjects_PreservedAsJsonElement()
    {
        var result = StreamingJsonParser.Parse("{\"edits\": [{\"oldText\": \"a\", \"newText\": \"b\"}]}");

        result.Should().ContainKey("edits");
        var edits = result["edits"];
        edits.Should().BeOfType<JsonElement>();
        var element = (JsonElement)edits!;
        element.ValueKind.Should().Be(JsonValueKind.Array);
        element.GetArrayLength().Should().Be(1);
        var first = element[0];
        first.ValueKind.Should().Be(JsonValueKind.Object);
        first.GetProperty("oldText").GetString().Should().Be("a");
        first.GetProperty("newText").GetString().Should().Be("b");
    }

    [Fact]
    public void NestedObjectsInDictionary_StillParsed()
    {
        var result = StreamingJsonParser.Parse("{\"outer\": {\"inner\": \"value\"}}");

        result.Should().ContainKey("outer");
        var outer = result["outer"] as Dictionary<string, object?>;
        outer.Should().NotBeNull();
        outer!["inner"].Should().Be("value");
    }

    [Fact]
    public void MixedTypesInArray_PreservedAsJsonElement()
    {
        var result = StreamingJsonParser.Parse("{\"items\": [1, \"two\", true, null]}");

        result.Should().ContainKey("items");
        var items = result["items"];
        items.Should().BeOfType<JsonElement>();
        var element = (JsonElement)items!;
        element.ValueKind.Should().Be(JsonValueKind.Array);
        element.GetArrayLength().Should().Be(4);
    }

    [Fact]
    public void PartialJson_ArrayUnclosed_Repaired()
    {
        var result = StreamingJsonParser.Parse("{\"items\": [1, 2");

        result.Should().ContainKey("items");
        var items = result["items"];
        items.Should().BeOfType<JsonElement>();
        var element = (JsonElement)items!;
        element.ValueKind.Should().Be(JsonValueKind.Array);
        element.GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void TrailingCommas_Handled()
    {
        var result = StreamingJsonParser.Parse("{\"a\": 1, \"b\": 2,}");

        result.Should().ContainKey("a");
        result.Should().ContainKey("b");
    }
}
