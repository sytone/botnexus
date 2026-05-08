using System.Text.Json;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Core.Tests.Utilities;

public class StreamingJsonParserTests
{
    [Fact]
    public void CompleteJson_ParsedCorrectly()
    {
        var result = StreamingJsonParser.Parse("{\"name\": \"test\", \"value\": 42}");

        result.ShouldContainKey("name");
        result["name"].ShouldBe("test");
        result.ShouldContainKey("value");
    }

    [Fact]
    public void PartialJson_UnclosedString_Repaired()
    {
        var result = StreamingJsonParser.Parse("{\"name\": \"partial");

        result.ShouldContainKey("name");
        result["name"].ShouldBe("partial");
    }

    [Fact]
    public void PartialJson_UnclosedObject_Repaired()
    {
        var result = StreamingJsonParser.Parse("{\"key\": \"value\"");

        result.ShouldContainKey("key");
        result["key"].ShouldBe("value");
    }

    [Fact]
    public void EmptyInput_ReturnsEmptyDict()
    {
        var result = StreamingJsonParser.Parse("");

        result.ShouldBeEmpty();
    }

    [Fact]
    public void WhitespaceInput_ReturnsEmptyDict()
    {
        var result = StreamingJsonParser.Parse("   ");

        result.ShouldBeEmpty();
    }

    [Fact]
    public void NestedObjects_Parsed()
    {
        var result = StreamingJsonParser.Parse("{\"outer\": {\"inner\": \"value\"}}");

        result.ShouldContainKey("outer");
        var outer = result["outer"] as Dictionary<string, object?>;
        outer.ShouldNotBeNull();
        outer!["inner"].ShouldBe("value");
    }

    [Fact]
    public void ArrayValues_Parsed()
    {
        var result = StreamingJsonParser.Parse("{\"items\": [1, 2, 3]}");

        result.ShouldContainKey("items");
        var items = result["items"];
        items.ShouldBeOfType<JsonElement>();
        var element = (JsonElement)items!;
        element.ValueKind.ShouldBe(JsonValueKind.Array);
        element.GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public void ArrayOfStrings_PreservedAsJsonElement()
    {
        var result = StreamingJsonParser.Parse("{\"command\": [\"echo\", \"hello\"]}");

        result.ShouldContainKey("command");
        var command = result["command"];
        command.ShouldBeOfType<JsonElement>();
        var element = (JsonElement)command!;
        element.ValueKind.ShouldBe(JsonValueKind.Array);
        element.GetArrayLength().ShouldBe(2);
        var items = element.EnumerateArray().ToList();
        items[0].GetString().ShouldBe("echo");
        items[1].GetString().ShouldBe("hello");
    }

    [Fact]
    public void ArrayOfObjects_PreservedAsJsonElement()
    {
        var result = StreamingJsonParser.Parse("{\"edits\": [{\"oldText\": \"a\", \"newText\": \"b\"}]}");

        result.ShouldContainKey("edits");
        var edits = result["edits"];
        edits.ShouldBeOfType<JsonElement>();
        var element = (JsonElement)edits!;
        element.ValueKind.ShouldBe(JsonValueKind.Array);
        element.GetArrayLength().ShouldBe(1);
        var first = element[0];
        first.ValueKind.ShouldBe(JsonValueKind.Object);
        first.GetProperty("oldText").GetString().ShouldBe("a");
        first.GetProperty("newText").GetString().ShouldBe("b");
    }

    [Fact]
    public void NestedObjectsInDictionary_StillParsed()
    {
        var result = StreamingJsonParser.Parse("{\"outer\": {\"inner\": \"value\"}}");

        result.ShouldContainKey("outer");
        var outer = result["outer"] as Dictionary<string, object?>;
        outer.ShouldNotBeNull();
        outer!["inner"].ShouldBe("value");
    }

    [Fact]
    public void MixedTypesInArray_PreservedAsJsonElement()
    {
        var result = StreamingJsonParser.Parse("{\"items\": [1, \"two\", true, null]}");

        result.ShouldContainKey("items");
        var items = result["items"];
        items.ShouldBeOfType<JsonElement>();
        var element = (JsonElement)items!;
        element.ValueKind.ShouldBe(JsonValueKind.Array);
        element.GetArrayLength().ShouldBe(4);
    }

    [Fact]
    public void PartialJson_ArrayUnclosed_Repaired()
    {
        var result = StreamingJsonParser.Parse("{\"items\": [1, 2");

        result.ShouldContainKey("items");
        var items = result["items"];
        items.ShouldBeOfType<JsonElement>();
        var element = (JsonElement)items!;
        element.ValueKind.ShouldBe(JsonValueKind.Array);
        element.GetArrayLength().ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void TrailingCommas_Handled()
    {
        var result = StreamingJsonParser.Parse("{\"a\": 1, \"b\": 2,}");

        result.ShouldContainKey("a");
        result.ShouldContainKey("b");
    }
}
