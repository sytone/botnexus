using System.Text.Json.Nodes;
using BotNexus.Gateway.Api.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Unit tests for <see cref="ConfigSchemaBuilder"/> (config-parity PBI 2/6 of #1579, issue #1610).
/// These exercise the builder directly -- no web host -- and assert the emitted UI schema reflects
/// the annotated <c>PlatformConfig</c> tree: scalar fields carry type/label/widget, nested objects
/// recurse, enum fields carry their options, secret fields are flagged, and the DateTimeInjection
/// canary from #1609 appears correctly.
///
/// Assertions target the stable <c>x-ui-*</c> overlay keys the builder controls, not the raw
/// JsonSchemaExporter substructure, so they stay robust across framework schema-shape changes.
/// </summary>
public sealed class ConfigSchemaBuilderTests
{
    // -- Helpers -------------------------------------------------------------

    private static JsonObject BuildSchema() => ConfigSchemaBuilder.Build();

    /// <summary>Returns the JSON-schema node for a property reachable by a dotted path of
    /// property names from the PlatformConfig root, descending through `properties` and, for
    /// dictionary-valued maps, through `additionalProperties`.</summary>
    private static JsonObject GetPropertyNode(JsonObject schema, params string[] propertyPath)
    {
        var node = schema["schema"]!.AsObject();
        foreach (var name in propertyPath)
        {
            // Descend into the current object's property bag.
            var props = node["properties"]?.AsObject()
                ?? throw new InvalidOperationException($"No 'properties' bag while resolving '{name}'.");
            var child = props[name]?.AsObject()
                ?? throw new InvalidOperationException($"Property '{name}' not found in schema.");

            // If this child is a dictionary/map schema, step through additionalProperties so the
            // next path segment resolves against the map's value-type object.
            node = child["additionalProperties"] is JsonObject map ? map : child;
        }

        return node;
    }

    /// <summary>Asserts a schema node's JSON-schema `type` includes the expected primitive. The
    /// exporter emits a scalar string for non-nullable types and an array (e.g. ["string","null"])
    /// for nullable ones, so both shapes are accepted.</summary>
    private static void ShouldHaveType(JsonObject node, string expected)
    {
        var type = node["type"]
            ?? throw new InvalidOperationException("Schema node has no 'type'.");

        if (type is JsonArray array)
        {
            array.Select(t => t!.GetValue<string>()).ShouldContain(expected);
            return;
        }

        type.GetValue<string>().ShouldBe(expected);
    }

    // -- 1. Stable, versioned envelope --------------------------------------

    [Fact]
    public void Build_EmitsStableVersionedEnvelope()
    {
        var schema = BuildSchema();

        schema["schemaVersion"]!.GetValue<string>().ShouldBe("1.0");
        schema["root"]!.GetValue<string>().ShouldBe("PlatformConfig");
        schema["schema"].ShouldBeOfType<JsonObject>();
    }

    // -- 2. Scalar field: type + label + widget -----------------------------

    [Fact]
    public void Build_ScalarField_CarriesTypeLabelAndWidget()
    {
        var schema = BuildSchema();

        // PlatformConfig.PlatformVersion -> [Display Name="Config schema version"],
        // [ConfigField Widget=Number, Group=general, Order=0], [DefaultValue(1)].
        var node = GetPropertyNode(schema, "version");

        ShouldHaveType(node, "integer");
        node["x-ui-label"]!.GetValue<string>().ShouldBe("Config schema version");
        node["x-ui-description"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        node["x-ui-widget"]!.GetValue<string>().ShouldBe("number");
        node["x-ui-group"]!.GetValue<string>().ShouldBe("general");
        node["x-ui-order"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public void Build_ScalarField_CarriesRealDefaultFromFreshInstance()
    {
        var schema = BuildSchema();

        // The live default of a fresh PlatformConfig().PlatformVersion is 1.
        var node = GetPropertyNode(schema, "version");

        node["x-ui-default"].ShouldNotBeNull();
        node["x-ui-default"]!.GetValue<int>().ShouldBe(1);
    }

    // -- 3. Nested object recurses ------------------------------------------

    [Fact]
    public void Build_NestedObject_Recurses()
    {
        var schema = BuildSchema();

        // gateway is a nested object; its ListenUrl scalar must be reachable and annotated.
        var listenUrl = GetPropertyNode(schema, "gateway", "listenUrl");

        ShouldHaveType(listenUrl, "string");
        listenUrl["x-ui-label"]!.GetValue<string>().ShouldBe("Listen URL");
        listenUrl["x-ui-widget"]!.GetValue<string>().ShouldBe("text");
    }

    [Fact]
    public void Build_DeeplyNestedObject_Recurses()
    {
        var schema = BuildSchema();

        // gateway.dateTimeInjection.enabled -- two levels deep through nested objects.
        var enabled = GetPropertyNode(schema, "gateway", "dateTimeInjection", "enabled");

        enabled["x-ui-widget"]!.GetValue<string>().ShouldBe("toggle");
    }

    // -- 4. Enum field carries its options ----------------------------------

    [Fact]
    public void Build_EnumField_CarriesOptions()
    {
        var schema = BuildSchema();

        // AgentDefinitionConfig.CacheRetention is a string enum {none, short, long}.
        var node = GetPropertyNode(schema, "agents", "cacheRetention");

        var options = node["x-ui-options"]?.AsArray()
            ?? throw new InvalidOperationException("Enum field did not carry x-ui-options.");

        var values = options.Select(o => o!.GetValue<string>()).ToArray();
        values.ShouldContain("none");
        values.ShouldContain("short");
        values.ShouldContain("long");
    }

    // -- 5. Secret field is flagged -----------------------------------------

    [Fact]
    public void Build_SecretField_IsFlagged()
    {
        var schema = BuildSchema();

        // ProviderConfig.ApiKey -> [ConfigField Widget=Secret, Secret=true].
        var node = GetPropertyNode(schema, "providers", "apiKey");

        node["x-ui-secret"]!.GetValue<bool>().ShouldBeTrue();
        node["x-ui-widget"]!.GetValue<string>().ShouldBe("secret");
    }

    [Fact]
    public void Build_NonSecretField_IsNotFlaggedSecret()
    {
        var schema = BuildSchema();

        // A plain text field must NOT carry the secret flag (renderers key off its presence/true).
        var node = GetPropertyNode(schema, "gateway", "listenUrl");

        // Either absent or explicitly false -- never true.
        var secret = node["x-ui-secret"];
        (secret is null || secret.GetValue<bool>() == false).ShouldBeTrue();
    }

    // -- 6. DateTimeInjection canary fields appear correctly -----------------

    [Fact]
    public void Build_DateTimeInjectionCanary_AllThreeFieldsAppearWithCorrectWidgets()
    {
        var schema = BuildSchema();

        var enabled = GetPropertyNode(schema, "gateway", "dateTimeInjection", "enabled");
        var timezone = GetPropertyNode(schema, "gateway", "dateTimeInjection", "timezone");
        var format = GetPropertyNode(schema, "gateway", "dateTimeInjection", "format");

        enabled["x-ui-widget"]!.GetValue<string>().ShouldBe("toggle");
        enabled["x-ui-label"]!.GetValue<string>().ShouldBe("Enable datetime injection");

        timezone["x-ui-widget"]!.GetValue<string>().ShouldBe("select");
        timezone["x-ui-label"]!.GetValue<string>().ShouldBe("Timezone");

        format["x-ui-widget"]!.GetValue<string>().ShouldBe("select");
        format["x-ui-label"]!.GetValue<string>().ShouldBe("Datetime format");
    }

    [Fact]
    public void Build_DateTimeInjectionFormat_CarriesIso8601Default()
    {
        var schema = BuildSchema();

        // Fresh DateTimeInjectionConfig().Format is "iso8601" (also a [DefaultValue]).
        var format = GetPropertyNode(schema, "gateway", "dateTimeInjection", "format");

        format["x-ui-default"]!.GetValue<string>().ShouldBe("iso8601");
    }

    // -- 7. Validation rules are surfaced -----------------------------------

    [Fact]
    public void Build_RangeAnnotatedField_SurfacesValidationBounds()
    {
        var schema = BuildSchema();

        // CronConfig.TickIntervalSeconds -> [Range(1, int.MaxValue)], default 60.
        var node = GetPropertyNode(schema, "cron", "tickIntervalSeconds");

        node["x-ui-validation"].ShouldNotBeNull();
        node["x-ui-validation"]!.AsObject()["minimum"]!.GetValue<double>().ShouldBe(1);
        node["x-ui-default"]!.GetValue<int>().ShouldBe(60);
    }
}
