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

        // Label from [Display(Name)], falling back to a humanized form of the property name so a
        // field with no annotation still renders "Listen URL" rather than the raw "listenUrl" JSON
        // key. This is what keeps the settings UI readable across the whole config tree without
        // requiring every one of the ~278 properties to be hand-annotated (and stops new unannotated
        // fields from regressing to raw-key labels).
        var label = display?.GetName();
        obj["x-ui-label"] = !string.IsNullOrWhiteSpace(label)
            ? label
            : Humanize(jsonProperty.Name);

        var description = display?.GetDescription();
        if (!string.IsNullOrWhiteSpace(description))
            obj["x-ui-description"] = description;

        // Group / order drive the sectioned layout and must be honored from EITHER attribute --
        // [ConfigField(Group=..., Order=...)] or [Display(GroupName=..., Order=...)] -- independently
        // of whether a [ConfigField] widget is present. (Previously these were emitted only inside
        // the `configField is not null` guard, so [Display(GroupName=...)] alone produced no
        // grouping and the field fell into the single unnamed catch-all group.)
        var group = configField is not null && !string.IsNullOrWhiteSpace(configField.Group)
            ? configField.Group
            : display?.GetGroupName();
        if (!string.IsNullOrWhiteSpace(group))
            obj["x-ui-group"] = group;

        var order = configField is not null && configField.Order != 0
            ? configField.Order
            : display?.GetOrder();
        if (order is { } orderValue)
            obj["x-ui-order"] = orderValue;

        // Widget / secret from [ConfigField].
        if (configField is not null)
        {
            obj["x-ui-widget"] = WidgetToken(configField.Widget);

            // Only emit the secret flag when set, so renderers can key off presence-or-true.
            if (configField.Secret || configField.Widget == ConfigFieldWidget.Secret)
                obj["x-ui-secret"] = true;

            // Dynamic option-source hint (#1893): a select field that draws its choices from a live
            // source (e.g. the provider's /api/models list) rather than a static enum. The renderer
            // resolves it and falls back to x-ui-options when no dynamic list is available.
            if (!string.IsNullOrWhiteSpace(configField.OptionsSource))
                obj["x-ui-options-source"] = configField.OptionsSource;
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

    /// <summary>
    /// Converts a JSON/CLR property name into a human-readable label when no explicit
    /// <c>[Display(Name)]</c> is provided: splits camelCase / PascalCase / snake_case / kebab-case
    /// boundaries, keeps consecutive-capital acronyms (URL, API, ID) intact, and title-cases the
    /// result. Examples: <c>listenUrl</c> -> "Listen URL", <c>defaultAgentId</c> -> "Default Agent ID",
    /// <c>api_key</c> -> "API Key". Purely presentational; the underlying JSON key is unchanged.
    /// </summary>
    public static string Humanize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;

        // Normalize snake_case / kebab-case separators to spaces first.
        var normalized = name.Replace('_', ' ').Replace('-', ' ');

        var sb = new System.Text.StringBuilder(normalized.Length + 8);
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (i > 0)
            {
                var prev = normalized[i - 1];
                // Insert a space at a lower->upper boundary (camelCase: "listenUrl").
                var lowerToUpper = char.IsLower(prev) && char.IsUpper(c);
                // Insert a space at a digit boundary ("agent2" / "2fa").
                var letterDigit = (char.IsLetter(prev) && char.IsDigit(c)) || (char.IsDigit(prev) && char.IsLetter(c));
                // Insert a space when leaving an acronym run into a new word ("URLPath" -> "URL Path").
                var acronymEnd = char.IsUpper(prev) && char.IsUpper(c)
                    && i + 1 < normalized.Length && char.IsLower(normalized[i + 1]);
                if (lowerToUpper || letterDigit || acronymEnd)
                    sb.Append(' ');
            }
            sb.Append(c);
        }

        // Collapse multiple spaces and title-case word-by-word, preserving all-caps acronyms.
        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var w = 0; w < words.Length; w++)
        {
            var word = words[w];
            // A known tech acronym renders all-caps regardless of source casing (api -> API).
            if (KnownAcronyms.Contains(word))
            {
                words[w] = word.ToUpperInvariant();
                continue;
            }
            // An already all-uppercase token (URL, ID, TZ) stays as-is; otherwise upper-case the
            // first letter and lower-case the remainder.
            if (word.Length > 1 && word.All(char.IsUpper))
                continue;
            words[w] = char.ToUpperInvariant(word[0]) + (word.Length > 1 ? word[1..].ToLowerInvariant() : string.Empty);
        }
        return string.Join(' ', words);
    }

    /// <summary>Common tech acronyms that should render fully upper-cased in humanized labels
    /// even when the source property casing is not all-caps (e.g. <c>apiKey</c> -> "API Key").</summary>
    private static readonly HashSet<string> KnownAcronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "API", "URL", "URI", "ID", "TZ", "UI", "IP", "TLS", "SSL", "HTTP", "HTTPS",
        "JSON", "JWT", "CORS", "CRON", "MCP", "QMD", "AI", "LLM", "SDK", "CLI", "OS",
        "DB", "WAL", "SMB", "UNC", "TTL", "CPU", "RAM", "WS", "SR",
    };

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
