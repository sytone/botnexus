using System.Text.Json.Nodes;
using BotNexus.Gateway.Api.Configuration;

namespace BotNexus.Gateway.Api.Tests;

/// <summary>
/// Unit coverage for <see cref="ConfigSchemaBuilder"/> - the pure, host-free builder behind
/// <c>GET /api/config/schema</c>. This is the config-parity seam (#1579) that the settings UI
/// renders from, so the emitted envelope shape and the per-property <c>x-ui-*</c> overlays are a
/// stable contract worth pinning. Covers the envelope contract, the schema payload, and overlay
/// presence (happy paths) plus the version-stability guard (sad path if it silently drifts).
/// </summary>
public sealed class ConfigSchemaBuilderTests
{
    [Fact]
    public void Build_EmitsVersionedEnvelope_WithRootAndSchema()
    {
        var envelope = ConfigSchemaBuilder.Build();

        Assert.Equal(ConfigSchemaBuilder.SchemaVersion, (string?)envelope["schemaVersion"]);
        Assert.Equal("PlatformConfig", (string?)envelope["root"]);
        Assert.NotNull(envelope["schema"]);
        Assert.IsType<JsonObject>(envelope["schema"]);
    }

    [Fact]
    public void SchemaVersion_IsStable_OneDotZero()
    {
        // Guard: bumping this is an intentional, breaking contract change for every downstream
        // renderer - it should never drift by accident.
        Assert.Equal("1.0", ConfigSchemaBuilder.SchemaVersion);
    }

    [Fact]
    public void Build_SchemaNode_DescribesAnObjectWithProperties()
    {
        var schema = Assert.IsType<JsonObject>(ConfigSchemaBuilder.Build()["schema"]);

        // The PlatformConfig root is an object; the exporter must emit a "properties" bag.
        Assert.True(schema.ContainsKey("properties"), "schema must expose a 'properties' node");
        var properties = Assert.IsType<JsonObject>(schema["properties"]);
        Assert.NotEmpty(properties);
    }

    [Fact]
    public void Build_OverlaysUiLabelMetadata_OnAtLeastOneProperty()
    {
        var json = ConfigSchemaBuilder.Build().ToJsonString();

        // The TransformSchemaNode overlay namespaces its UI metadata under x-ui-*; if the overlay
        // silently stopped running the whole settings UI would regress to raw JSON keys.
        Assert.Contains("x-ui-label", json);
    }

    [Fact]
    public void Build_IsDeterministic_ProducesEqualJsonAcrossCalls()
    {
        // Pure, side-effect-free builder: two calls must yield byte-identical documents.
        Assert.Equal(
            ConfigSchemaBuilder.Build().ToJsonString(),
            ConfigSchemaBuilder.Build().ToJsonString());
    }
}
