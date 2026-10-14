using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

public class AnthropicRequestCacheBudgetTests
{
    private const string Boundary = "\n<!-- BOTNEXUS_CACHE_BOUNDARY -->\n";
    private const string Preamble = "You are Claude Code, Anthropic's official CLI for Claude.";

    public static IEnumerable<object?[]> RequestCases()
    {
        foreach (var oauth in new[] { false, true })
        foreach (var tools in new[] { false, true })
        foreach (var system in new string?[] { null, "", "Stable prefix", "Stable prefix" + Boundary + "Dynamic tail", Boundary + "Dynamic tail", "Stable prefix" + Boundary, Boundary, "   " })
        foreach (var historyLength in new[] { 0, 1, 7 })
        foreach (var retention in new[] { CacheRetention.None, CacheRetention.Short, CacheRetention.Long })
        foreach (var baseUrl in new[] { "https://api.anthropic.com", "https://proxy.example.com" })
            yield return [oauth, tools, system, historyLength, retention, baseUrl];
    }

    [Theory]
    [MemberData(nameof(RequestCases))]
    public void BuildRequestBody_SerializedRequest_RespectsSharedBudgetAndPreservesContent(
        bool oauth, bool tools, string? system, int historyLength, CacheRetention retention, string baseUrl)
    {
        var model = TestHelpers.MakeModel() with { BaseUrl = baseUrl };
        var messages = Enumerable.Range(0, historyLength)
            .Select(i => (Message)new UserMessage(new UserMessageContent($"User message {i}"), i))
            .ToList();
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"query":{"type":"string"}}}""");
        var context = new Context(system, messages,
            tools ? [new Tool("read", "Read content", schema.RootElement.Clone())] : null);
        var body = SerializedBody(model, context, oauth, retention);
        var uncached = SerializedBody(model, context, oauth, CacheRetention.None);

        // Compare every field, allowing only cache annotations and string-to-text-block wrapping.
        JsonNode.DeepEquals(NormalizeContent(body), NormalizeContent(uncached)).ShouldBeTrue();
        FindMarkers(body["tools"]).ShouldBeEmpty();

        var systemBlocks = body["system"] as JsonArray;
        var expectedSystemMarkers = 0;
        if (systemBlocks is not null)
        {
            foreach (var block in systemBlocks)
            {
                var obj = block.ShouldBeOfType<JsonObject>();
                var text = RequiredString(obj["text"]);
                var stable = text != "Dynamic tail";
                obj.ContainsKey("cache_control").ShouldBe(retention != CacheRetention.None && stable);
                if (stable && retention != CacheRetention.None)
                    expectedSystemMarkers++;
            }
            if (oauth)
                RequiredString(systemBlocks[0].ShouldBeOfType<JsonObject>()["text"]).ShouldBe(Preamble);
        }

        var markers = FindMarkers(body).ToList();
        markers.Count.ShouldBeLessThanOrEqualTo(4);
        var expectedMessageMarkers = retention == CacheRetention.None
            ? 0 : Math.Min(historyLength, Math.Min(3, 4 - expectedSystemMarkers));
        markers.Count.ShouldBe(expectedSystemMarkers + expectedMessageMarkers);
        var messageNodes = body["messages"].ShouldBeOfType<JsonArray>();
        for (var i = 0; i < messageNodes.Count; i++)
            FindMarkers(messageNodes[i]).Count().ShouldBe(i >= historyLength - expectedMessageMarkers ? 1 : 0);

        foreach (var marker in markers)
        {
            var value = marker.ShouldBeOfType<JsonObject>();
            RequiredString(value["type"]).ShouldBe("ephemeral");
            var longTtl = retention == CacheRetention.Long && baseUrl == "https://api.anthropic.com";
            value.ContainsKey("ttl").ShouldBe(longTtl);
            if (longTtl)
                RequiredString(value["ttl"]).ShouldBe("1h");
        }
    }

    [Theory]
    [InlineData(false, "apikey-direct.json")]
    [InlineData(true, "oauth-direct.json")]
    public void BuildRequestBody_ShortHistory_PreservesExistingSnapshotBody(bool oauth, string filename)
    {
        var model = TestHelpers.MakeModel(maxTokens: 12288);
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"count":{"type":"integer","minimum":1,"maximum":100}},"required":["count"]}""");
        var context = new Context(
            "You are a helpful assistant operating inside BotNexus tests. Reply concisely and use tools when asked.",
            [new UserMessage(new UserMessageContent("List the first three prime numbers."), 1_700_000_000_000L)],
            [new Tool("list_primes", "Returns the first N prime numbers.", schema.RootElement.Clone())]);
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Requests", "messages", "claude-sonnet-4", filename);
        var snapshot = JsonNode.Parse(File.ReadAllText(path)).ShouldBeOfType<JsonObject>();
        JsonNode.DeepEquals(SerializedBody(model, context, oauth, CacheRetention.Short), snapshot["body"]).ShouldBeTrue();
    }

    [Theory]
    [InlineData(CacheRetention.None)]
    [InlineData(CacheRetention.Short)]
    [InlineData(CacheRetention.Long)]
    public void BuildRequestBody_NestedToolContent_PreservesPayloadKeysAndBudget(CacheRetention retention)
    {
        var model = TestHelpers.MakeModel();
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"cache_control":{"type":"string"}}}""");
        var arguments = new Dictionary<string, object?> { ["cache_control"] = "payload, not an annotation" };
        var assistant = new AssistantMessage(
            [new TextContent("Calling tool"), new ToolCallContent("toolu_1", "read", arguments)],
            model.Api, model.Provider, model.Id, new Usage(), StopReason.ToolUse, null, null, 2);
        var context = new Context("Stable prefix" + Boundary + "Dynamic tail",
            [new UserMessage(new UserMessageContent("hello"), 1), assistant,
             new ToolResultMessage("toolu_1", "read", [new TextContent("result"), new ImageContent("aGVsbG8=", "image/png")], false, 3),
             new UserMessage(new UserMessageContent("continue"), 4)],
            [new Tool("read", "Read content", schema.RootElement.Clone())]);
        var body = SerializedBody(model, context, true, retention);
        var uncached = SerializedBody(model, context, true, CacheRetention.None);
        JsonNode.DeepEquals(NormalizeContent(body), NormalizeContent(uncached)).ShouldBeTrue();
        FindMarkers(body).Count().ShouldBe(retention == CacheRetention.None ? 0 : 4);
        body["tools"].ShouldBeOfType<JsonArray>()[0].ShouldBeOfType<JsonObject>()["input_schema"]
            .ShouldBeOfType<JsonObject>()["properties"].ShouldBeOfType<JsonObject>()
            .ContainsKey("cache_control").ShouldBeTrue();
        var blocks = body["messages"].ShouldBeOfType<JsonArray>()[1].ShouldBeOfType<JsonObject>()["content"].ShouldBeOfType<JsonArray>();
        RequiredString(blocks[1].ShouldBeOfType<JsonObject>()["input"].ShouldBeOfType<JsonObject>()["cache_control"])
            .ShouldBe("payload, not an annotation");
    }

    private static string RequiredString(JsonNode? node)
    {
        node.ShouldNotBeNull();
        node.ShouldBeAssignableTo<JsonValue>();
        return node.GetValue<string>();
    }

    private static JsonObject SerializedBody(LlmModel model, Context context, bool oauth, CacheRetention retention)
        => JsonNode.Parse(AnthropicRequestBuilder.BuildRequestBody(
            model, context, new StreamOptions { CacheRetention = retention }, null, oauth, _ => false)
            .ToJsonString()).ShouldBeOfType<JsonObject>();

    // Count keys recursively, including null-valued annotations (None must omit the key entirely).
    private static IEnumerable<JsonNode?> FindMarkers(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (property.Key == "cache_control")
                    yield return property.Value;
                else if (property.Key is not ("input_schema" or "input" or "source"))
                    foreach (var marker in FindMarkers(property.Value))
                        yield return marker;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            foreach (var marker in FindMarkers(child))
                yield return marker;
        }
    }

    private static JsonNode? NormalizeContent(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var property in obj)
            {
                if (property.Key == "cache_control")
                    continue;
                result[property.Key] = property.Key == "content" && property.Value is JsonValue text && text.TryGetValue<string>(out var value)
                    ? new JsonArray(new JsonObject { ["type"] = "text", ["text"] = value })
                    : property.Key is "input_schema" or "input" or "source"
                        ? property.Value?.DeepClone()
                        : NormalizeContent(property.Value);
            }
            return result;
        }
        if (node is JsonArray array)
            return new JsonArray(array.Select(NormalizeContent).ToArray());
        return node?.DeepClone();
    }
}
