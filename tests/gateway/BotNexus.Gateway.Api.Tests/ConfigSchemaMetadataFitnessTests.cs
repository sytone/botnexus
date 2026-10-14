using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Configuration;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Api.Tests;

/// <summary>
/// Guards member metadata parity for dictionary properties, not dictionary value schemas.
/// Expectations follow the existing annotations and serializer contract, without requiring annotations.
/// </summary>
public sealed class ConfigSchemaMetadataFitnessTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>Source-backed dictionary categories at root, nested objects, and dictionary values.</summary>
    public static IEnumerable<object[]> DictionaryProperties()
    {
        (Type Type, string Path)[] scopes =
        [
            (typeof(PlatformConfig), ""),
            (typeof(GatewaySettingsConfig), "gateway"),
            (typeof(ProviderChatConfig), "providers/*/chat"),
            (typeof(CrossWorldInboundConfig), "gateway/crossWorld/inbound"),
            (typeof(LocationConfig), "gateway/locations/*"),
            (typeof(AgentDefinitionConfig), "agents/*"),
            (typeof(ChannelConfig), "channels/*"),
            (typeof(CronJobConfig), "cron/jobs/*"),
            (typeof(PromptTemplateConfig), "promptTemplates/*"),
        ];
        foreach (var (type, path) in scopes)
        {
            foreach (var property in SerializerOptions.GetTypeInfo(type).Properties)
            {
                // JsonIgnore and extension-data bags are not named serialized properties.
                if (property.Get is null || property.IsExtensionData)
                    continue;
                if (property.PropertyType.GetInterfaces().Append(property.PropertyType).Any(candidate =>
                    candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) &&
                    candidate.GetGenericArguments()[0] == typeof(string)))
                    yield return [type, path, property.Name];
            }
        }
    }

    [Theory]
    [MemberData(nameof(DictionaryProperties))]
    public void Build_DictionaryProperty_MatchesMemberMetadata(Type declaringType, string path, string jsonName)
        => AssertMemberMetadata(declaringType, path, jsonName);

    [Theory]
    [InlineData(typeof(PlatformConfig), "", "version")]
    [InlineData(typeof(GatewaySettingsConfig), "gateway", "listenUrl")]
    [InlineData(typeof(ProviderConfig), "providers/*", "apiKey")]
    [InlineData(typeof(ProviderConfig), "providers/*", "defaultModel")]
    public void Build_OrdinaryProperty_MatchesSameMemberMetadata(Type declaringType, string path, string jsonName)
        => AssertMemberMetadata(declaringType, path, jsonName);

    [Theory]
    [MemberData(nameof(DictionaryProperties))]
    public void Build_DictionaryProperty_DefaultMatchesFreshDto(Type declaringType, string path, string jsonName)
    {
        var member = GetMember(declaringType, jsonName);
        var instance = Activator.CreateInstance(declaringType)
            ?? throw new InvalidOperationException($"Cannot construct {declaringType.Name}.");
        var value = member.GetValue(instance) ?? member.GetCustomAttribute<DefaultValueAttribute>()?.Value;
        var expected = value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), SerializerOptions);
        JsonNode.DeepEquals(GetPropertySchema(path, jsonName)["x-ui-default"], expected)
            .ShouldBeTrue($"{declaringType.Name}.{member.Name} must retain its existing runtime default");
    }

    [Fact]
    public void Build_SecretDictionary_NullDefaultIsAbsentAndMetadataStaysOnOwner()
    {
        new CrossWorldInboundConfig().ApiKeys.ShouldBeNull();
        var dictionary = GetPropertySchema("gateway/crossWorld/inbound", "apiKeys");
        ((bool?)dictionary["x-ui-secret"]).ShouldBe(true);
        ((string?)dictionary["x-ui-widget"]).ShouldBe("secret");
        dictionary.ContainsKey("x-ui-default").ShouldBeFalse();
        var value = dictionary["additionalProperties"].ShouldBeOfType<JsonObject>();
        value.ContainsKey("x-ui-secret").ShouldBeFalse();
        value.ContainsKey("x-ui-default").ShouldBeFalse();
    }

    [Fact]
    public void Build_ObjectDictionary_NestedSecretOwnerIsPreserved()
    {
        new GatewaySettingsConfig().ApiKeys.ShouldBeNull();
        new ApiKeyConfig().ApiKey.ShouldBeNull();
        var dictionary = GetPropertySchema("gateway", "apiKeys");
        dictionary.ContainsKey("x-ui-secret").ShouldBeFalse();
        dictionary.ContainsKey("x-ui-default").ShouldBeFalse();
        var secret = GetPropertySchema("gateway/apiKeys/*", "apiKey");
        ((bool?)secret["x-ui-secret"]).ShouldBe(true);
        ((string?)secret["x-ui-widget"]).ShouldBe("secret");
        secret.ContainsKey("x-ui-default").ShouldBeFalse();
        AssertMemberMetadata(typeof(ApiKeyConfig), "gateway/apiKeys/*", "apiKey");
    }

    [Fact]
    public void Build_IgnoredAndExtensionDataMembers_AreNotNamedSchemaProperties()
    {
        var root = GetObjectSchema("")["properties"].ShouldBeOfType<JsonObject>();
        root.ContainsKey("agentDefaults").ShouldBeFalse();
        root.ContainsKey("agentRawElements").ShouldBeFalse();
        var channel = GetObjectSchema("channels/*")["properties"].ShouldBeOfType<JsonObject>();
        channel.ContainsKey("additionalSettings").ShouldBeFalse();
    }

    private static void AssertMemberMetadata(Type declaringType, string path, string jsonName)
    {
        var member = GetMember(declaringType, jsonName);
        var node = GetPropertySchema(path, jsonName);
        var display = member.GetCustomAttribute<DisplayAttribute>();
        var field = member.GetCustomAttribute<ConfigFieldAttribute>();
        var label = display?.GetName();
        ((string?)node["x-ui-label"]).ShouldBe(string.IsNullOrWhiteSpace(label)
            ? ConfigSchemaBuilder.Humanize(jsonName) : label);
        AssertOptionalString(node, "x-ui-description", display?.GetDescription());
        AssertOptionalString(node, "x-ui-group", !string.IsNullOrWhiteSpace(field?.Group)
            ? field.Group : display?.GetGroupName());
        ((int?)node["x-ui-order"]).ShouldBe(field is not null && field.Order != 0
            ? field.Order : display?.GetOrder());
        ((string?)node["x-ui-widget"]).ShouldBe(field is null ? null : field.Widget.ToString().ToLowerInvariant());
        ((bool?)node["x-ui-secret"]).ShouldBe(field is not null &&
            (field.Secret || field.Widget == ConfigFieldWidget.Secret) ? true : null);
        AssertOptionalString(node, "x-ui-options-source", field?.OptionsSource);
    }

    private static void AssertOptionalString(JsonObject node, string key, string? expected)
        => ((string?)node[key]).ShouldBe(string.IsNullOrWhiteSpace(expected) ? null : expected);

    private static PropertyInfo GetMember(Type type, string jsonName)
        => SerializerOptions.GetTypeInfo(type).Properties.Single(property => property.Name == jsonName)
            .AttributeProvider as PropertyInfo
            ?? throw new InvalidOperationException($"Missing property reflection metadata for {type.Name}.{jsonName}.");

    private static JsonObject GetPropertySchema(string path, string jsonName)
        => GetObjectSchema(path)["properties"].ShouldBeOfType<JsonObject>()[jsonName].ShouldBeOfType<JsonObject>();

    private static JsonObject GetObjectSchema(string path)
    {
        var root = ConfigSchemaBuilder.Build()["schema"].ShouldBeOfType<JsonObject>();
        var node = root;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = ResolveReference(root, node);
            node = segment == "*"
                ? node["additionalProperties"].ShouldBeOfType<JsonObject>()
                : node["properties"].ShouldBeOfType<JsonObject>()[segment].ShouldBeOfType<JsonObject>();
        }
        return ResolveReference(root, node);
    }

    private static JsonObject ResolveReference(JsonObject root, JsonObject node)
    {
        if ((string?)node["$ref"] is not { } reference)
            return node;
        reference.ShouldStartWith("#/");
        JsonNode target = root;
        foreach (var segment in reference[2..].Split('/'))
            target = target[segment.Replace("~1", "/").Replace("~0", "~")]
                ?? throw new InvalidOperationException($"Unresolved schema reference {reference}.");
        return target.ShouldBeOfType<JsonObject>();
    }
}
