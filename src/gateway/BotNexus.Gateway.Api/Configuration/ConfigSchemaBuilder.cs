using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Api.Configuration;

/// <summary>
/// Builds the read-only UI schema served by <c>GET /api/config/schema</c> (config-parity PBI 2/6
/// of #1579). It reflects over the annotated <see cref="PlatformConfig"/> tree and produces a
/// stable, versioned JSON document a settings renderer can consume to draw an editor without any
/// hand-written form code.
/// </summary>
/// <remarks>
/// <para>
/// The type+validation half comes from <see cref="JsonSchemaExporter"/> (.NET's first-party
/// reflection-driven JSON-schema generator). A <see cref="JsonSchemaExporterOptions.TransformSchemaNode"/>
/// callback then overlays the richer UI metadata per property: the <c>[Display]</c> label and
/// description, the <c>[ConfigField]</c> widget / group / order / secret flag, validation bounds
/// (<c>[Range]</c>), the real default (read from a fresh instance of the declaring type), and enum
/// options. The overlay keys are namespaced under <c>x-ui-*</c> so they coexist with standard
/// JSON-schema keywords and form the stable contract downstream renderers depend on.
/// </para>
/// <para>
/// This is a pure, side-effect-free builder with no host dependencies, so it is unit-testable
/// without spinning the web host. The controller endpoint is a thin pass-through to <see cref="Build"/>.
/// </para>
/// </remarks>
public static class ConfigSchemaBuilder
{
    /// <summary>
    /// The schema-shape contract version. Bumped only when the emitted envelope or the meaning of
    /// the <c>x-ui-*</c> overlay keys changes incompatibly, so consumers can branch on it safely.
    /// </summary>
    public const string SchemaVersion = "1.0";

    private static readonly JsonSerializerOptions SchemaSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // JsonSchemaExporter requires an explicit resolver before the options are frozen; the
        // reflection-based resolver is what exposes each property's PropertyInfo (and its
        // attributes) to the TransformSchemaNode overlay. Plain options (not the Web defaults)
        // keep numeric "type" entries clean ("integer" rather than ["string","integer"]).
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>
    /// Builds the versioned UI schema envelope for the platform configuration tree.
    /// </summary>
    /// <returns>
    /// A JSON object of the shape
    /// <c>{ "schemaVersion": "1.0", "root": "PlatformConfig", "schema": { ... } }</c> where
    /// <c>schema</c> is the JsonSchemaExporter output with per-property <c>x-ui-*</c> overlays.
    /// </returns>
    public static JsonObject Build()
    {
        var exporterOptions = new JsonSchemaExporterOptions
        {
            // Overlay UI metadata onto every property node as the exporter walks the type graph.
            TransformSchemaNode = OverlayUiMetadata,
        };

        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(
            SchemaSerializerOptions,
            typeof(PlatformConfig),
            exporterOptions);

        return new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["root"] = nameof(PlatformConfig),
            ["schema"] = schema,
        };
    }

    /// <summary>
    /// Transform callback invoked by <see cref="JsonSchemaExporter"/> for each schema node. When the
    /// node corresponds to a CLR property it overlays the UI metadata; otherwise the node is returned
    /// unchanged.
    /// </summary>
    private static JsonNode OverlayUiMetadata(JsonSchemaExporterContext context, JsonNode node)
    {
        // Only property nodes carry UI metadata; the root/object containers and array element
        // schemas have no [ConfigField]/[Display] of their own.
        if (node is not JsonObject obj || context.PropertyInfo is not { } jsonProperty)
            return node;

        // The reflection-based resolver exposes the underlying PropertyInfo here, which is where the
        // annotations live. Guard defensively in case a custom resolver supplies something else.
        if (jsonProperty.AttributeProvider is not MemberInfo member)
            return node;

        var display = member.GetCustomAttribute<DisplayAttribute>();
        var configField = member.GetCustomAttribute<ConfigFieldAttribute>();

        // Label / description from [Display].
        var label = display?.GetName();
        if (!string.IsNullOrWhiteSpace(label))
            obj["x-ui-label"] = label;

        var description = display?.GetDescription();
        if (!string.IsNullOrWhiteSpace(description))
            obj["x-ui-description"] = description;

        // Widget / group / order / secret from [ConfigField], falling back to [Display] for
        // group/order so an annotated [Display(GroupName=..., Order=...)] still drives layout.
        if (configField is not null)
        {
            obj["x-ui-widget"] = WidgetToken(configField.Widget);

            var group = !string.IsNullOrWhiteSpace(configField.Group)
                ? configField.Group
                : display?.GetGroupName();
            if (!string.IsNullOrWhiteSpace(group))
                obj["x-ui-group"] = group;

            var order = configField.Order != 0 ? configField.Order : display?.GetOrder();
            if (order is { } orderValue)
                obj["x-ui-order"] = orderValue;

            // Only emit the secret flag when set, so renderers can key off presence-or-true.
            if (configField.Secret || configField.Widget == ConfigFieldWidget.Secret)
                obj["x-ui-secret"] = true;
        }

        // Validation bounds from [Range] (the exporter does not surface DataAnnotations itself).
        OverlayValidation(member, obj);

        // Enum options: mirror the JSON-schema enum values the exporter already emitted so a Select
        // renderer has the option list without re-deriving the [JsonStringEnumMemberName] mapping.
        OverlayEnumOptions(obj);

        // Real default from a fresh instance of the declaring type (preferred over [DefaultValue]
        // so the schema reflects the actual runtime default even when the attribute is omitted).
        OverlayDefault(member, obj);

        return node;
    }

    private static void OverlayValidation(MemberInfo member, JsonObject obj)
    {
        var range = member.GetCustomAttribute<RangeAttribute>();
        if (range is null)
            return;

        var validation = new JsonObject();
        if (range.Minimum is IConvertible min)
            validation["minimum"] = JsonValue.Create(Convert.ToDouble(min, System.Globalization.CultureInfo.InvariantCulture));
        if (range.Maximum is IConvertible max)
            validation["maximum"] = JsonValue.Create(Convert.ToDouble(max, System.Globalization.CultureInfo.InvariantCulture));

        if (validation.Count > 0)
            obj["x-ui-validation"] = validation;
    }

    private static void OverlayEnumOptions(JsonObject obj)
    {
        // The exporter emits enum-typed properties with a JSON-schema "enum" array (string values
        // already mapped via the active enum converter). Surface those as x-ui-options.
        if (obj["enum"] is not JsonArray enumValues || enumValues.Count == 0)
            return;

        var options = new JsonArray();
        foreach (var value in enumValues)
        {
            if (value is null)
                continue;
            options.Add(JsonValue.Create(value.GetValue<string>()));
        }

        if (options.Count > 0)
            obj["x-ui-options"] = options;
    }

    private static void OverlayDefault(MemberInfo member, JsonObject obj)
    {
        if (member is not PropertyInfo property || property.DeclaringType is null)
            return;

        var defaultValue = ResolveDefaultValue(property);
        if (defaultValue is null)
            return;

        var node = JsonSerializer.SerializeToNode(defaultValue, defaultValue.GetType(), SchemaSerializerOptions);
        if (node is not null)
            obj["x-ui-default"] = node;
    }

    /// <summary>
    /// Resolves the effective default for a property: the live value from a freshly constructed
    /// instance of its declaring type when one can be created, otherwise the <c>[DefaultValue]</c>
    /// attribute. Returns null when no meaningful default exists (so callers omit the key).
    /// </summary>
    private static object? ResolveDefaultValue(PropertyInfo property)
    {
        var declaringType = property.DeclaringType!;

        // Prefer the real runtime default by instantiating the declaring type. The config POCOs all
        // have public parameterless constructors; if one ever does not, fall back to [DefaultValue].
        if (property.CanRead && declaringType.GetConstructor(Type.EmptyTypes) is not null)
        {
            try
            {
                var instance = Activator.CreateInstance(declaringType);
                var value = instance is null ? null : property.GetValue(instance);
                // A null property default is not informative for the UI -- omit it.
                if (value is not null)
                    return value;
            }
            catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException or MemberAccessException)
            {
                // Fall through to the attribute-based default below.
            }
        }

        var defaultValueAttribute = property.GetCustomAttribute<DefaultValueAttribute>();
        return defaultValueAttribute?.Value;
    }

    private static string WidgetToken(ConfigFieldWidget widget) => widget switch
    {
        ConfigFieldWidget.Toggle => "toggle",
        ConfigFieldWidget.Text => "text",
        ConfigFieldWidget.Number => "number",
        ConfigFieldWidget.Select => "select",
        ConfigFieldWidget.Secret => "secret",
        _ => "text",
    };
}
