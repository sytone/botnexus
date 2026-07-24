using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Guards the schema/runtime enum contract: enum-typed config properties are serialized and
/// deserialized as strings at runtime (via property- or type-level <c>JsonStringEnumConverter</c>),
/// so the generated JSON schema must advertise those enums as strings with their wire names — not
/// integers. Without a <c>JsonStringEnumConverter</c> on the schema-generation
/// <c>SerializerOptions</c>, NJsonSchema emitted integer enums, causing valid documented string
/// values to fail schema validation.
/// </summary>
public sealed class SchemaEnumParityTests
{
    [Theory]
    [InlineData("CacheRetention", "none", "short", "long")]
    [InlineData("AgentKind", "Named", "SubAgent")]
    public void GeneratedSchema_EnumDefinition_IsStringWithWireNames(string definitionName, params string[] expectedValues)
    {
        var schema = JsonNode.Parse(PlatformConfigSchema.GenerateSchemaJson())!;

        var definition = schema["definitions"]?[definitionName]?.AsObject();
        Assert.NotNull(definition);

        Assert.Equal("string", definition!["type"]?.GetValue<string>());

        var enumValues = definition["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(expectedValues, enumValues);
    }
}
