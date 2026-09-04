using System.Text.Json.Nodes;
using BotNexus.Gateway.Api.Configuration;

namespace BotNexus.Gateway.Api.Tests;

/// <summary>
/// AC6 of #2854: the per-capability provider objects must reach the config schema with UI metadata
/// complete enough for the portal settings editor to render them (the #2056 metadata expectations).
/// </summary>
/// <remarks>
/// A nested capability object is exactly the shape that goes missing silently: the exporter walks
/// the type graph, so a property with no annotations still appears, but it renders as a raw JSON
/// key with no label, group or widget hint. These tests pin the overlay reaching every new field
/// rather than merely the object existing.
/// </remarks>
public sealed class ProviderCapabilitySchemaTests
{
    private static JsonObject ProviderProperties()
    {
        var schema = (JsonObject)ConfigSchemaBuilder.Build()["schema"]!;

        // PlatformConfig.Providers is Dictionary<string, ProviderConfig>; the exporter emits the
        // value schema under additionalProperties, either inline or via a $defs reference.
        var providers = (JsonObject)((JsonObject)schema["properties"]!)["providers"]!;
        var valueSchema = Resolve(schema, providers["additionalProperties"]);
        return (JsonObject)valueSchema["properties"]!;
    }

    private static JsonObject Resolve(JsonObject root, JsonNode? node)
    {
        var obj = (JsonObject)node!;

        // Unwrap a nullable union ("anyOf": [ {...}, { "type": "null" } ]) before following a $ref.
        if (obj["anyOf"] is JsonArray anyOf)
        {
            foreach (var branch in anyOf)
            {
                if (branch is JsonObject candidate && (string?)candidate["type"] != "null")
                    return Resolve(root, candidate);
            }
        }

        if ((string?)obj["$ref"] is not { } reference)
            return obj;

        // "#/$defs/Name" -> root["$defs"]["Name"]
        var name = reference[(reference.LastIndexOf('/') + 1)..];
        return Resolve(root, ((JsonObject)root["$defs"]!)[name]);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("embeddings")]
    public void ProviderSchema_ExposesTheCapabilityObject_WithUiMetadata(string capability)
    {
        var property = (JsonObject)ProviderProperties()[capability]!;

        Assert.True(property.ContainsKey("x-ui-label"), $"{capability} must carry an x-ui-label");
        Assert.True(property.ContainsKey("x-ui-group"), $"{capability} must carry an x-ui-group");
        Assert.True(property.ContainsKey("x-ui-description"), $"{capability} must carry an x-ui-description");
    }

    [Theory]
    [InlineData("chat", "api")]
    [InlineData("chat", "defaultModel")]
    [InlineData("chat", "models")]
    [InlineData("embeddings", "api")]
    [InlineData("embeddings", "model")]
    [InlineData("embeddings", "dimensions")]
    public void CapabilityObject_Fields_CarryRenderableMetadata(string capability, string field)
    {
        var schema = (JsonObject)ConfigSchemaBuilder.Build()["schema"]!;
        var capabilityObject = Resolve(schema, ProviderProperties()[capability]);
        var property = (JsonObject)((JsonObject)capabilityObject["properties"]!)[field]!;

        Assert.True(property.ContainsKey("x-ui-label"), $"{capability}.{field} must carry an x-ui-label");
        Assert.True(property.ContainsKey("x-ui-widget"), $"{capability}.{field} must carry an x-ui-widget");
    }

    [Fact]
    public void ChatDefaultModel_KeepsTheDynamicModelOptionsSource()
    {
        // The flat defaultModel drove the portal's model picker from the live /api/models list.
        // Losing x-ui-options-source in the move to the nested object would silently downgrade the
        // picker to a free-text box.
        var schema = (JsonObject)ConfigSchemaBuilder.Build()["schema"]!;
        var chat = Resolve(schema, ProviderProperties()["chat"]);
        var defaultModel = (JsonObject)((JsonObject)chat["properties"]!)["defaultModel"]!;

        Assert.Equal("models", (string?)defaultModel["x-ui-options-source"]);
    }
}
