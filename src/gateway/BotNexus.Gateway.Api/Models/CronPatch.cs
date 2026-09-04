using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Api.Models;

/// <summary>
/// A JSON property that distinguishes <b>absent</b> from <b>present-and-null</b> (#3808).
/// </summary>
/// <remarks>
/// <para>
/// This type exists because <c>PUT /api/cron/{jobId}</c> previously bound the domain record
/// <c>CronJob</c> directly, so a field the caller omitted deserialised to its CLR default and was
/// written over the stored value. For <c>bool</c> that default is <c>false</c> and for a reference
/// or nullable type it is <c>null</c> - both indistinguishable from an explicit "set it to that".
/// The result was that an edit of a job's schedule silently cleared its failure alerting, its
/// one-shot disposition, its expiry and its execution class, none of which the caller mentioned.
/// </para>
/// <para>
/// The mechanism is deliberately the converter's <i>invocation</i>, not a sentinel value: System.Text.Json
/// calls a property's converter only when that property actually appears in the payload, so
/// <see cref="IsSet"/> can be true only for a property the caller wrote. An absent property leaves
/// the field at <c>default</c>, which is <c>IsSet == false</c>. There is no value a caller can send
/// that forges absence, and no absence that can be mistaken for a value.
/// </para>
/// <para>
/// This is the durable half of the fix. The alternative - extending the controller's
/// <c>request with { ... }</c> carry-over block by hand - had already been done twice (#2554,
/// #3575) and drifts again every time a column is added to <c>CronJob</c>, because a newly added
/// field is preserved only if someone remembers to name it. Under this type the default for an
/// unmentioned field is "leave it alone", so forgetting is safe.
/// </para>
/// </remarks>
/// <typeparam name="T">The underlying property type.</typeparam>
[JsonConverter(typeof(CronPatchConverterFactory))]
public readonly record struct CronPatch<T>
{
    /// <summary>Whether the property was present in the request body at all.</summary>
    public bool IsSet { get; init; }

    /// <summary>The supplied value. Meaningless unless <see cref="IsSet"/> is <c>true</c>.</summary>
    public T? Value { get; init; }

    /// <summary>An absent property - the default, and the one that preserves stored state.</summary>
    public static CronPatch<T> Unset => default;

    /// <summary>An explicitly supplied property, including an explicit null.</summary>
    /// <param name="value">The value the caller sent.</param>
    /// <returns>A set patch carrying <paramref name="value"/>.</returns>
    public static CronPatch<T> Set(T? value) => new() { IsSet = true, Value = value };

    /// <summary>
    /// The effective value: what the caller supplied, or <paramref name="fallback"/> when the
    /// property was absent. This is the whole point of the type - the omitted-field-preserves rule
    /// the <c>CronTool</c> seam has applied since #2634, expressed once instead of per field.
    /// </summary>
    /// <param name="fallback">The stored value to retain when the caller said nothing.</param>
    /// <returns>The value to persist.</returns>
    public T? Or(T? fallback) => IsSet ? Value : fallback;
}

/// <summary>
/// Supplies the converter that makes <see cref="CronPatch{T}"/> presence-aware (#3808).
/// </summary>
public sealed class CronPatchConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType
           && typeToConvert.GetGenericTypeDefinition() == typeof(CronPatch<>);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var valueType = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter)Activator.CreateInstance(
            typeof(CronPatchConverter<>).MakeGenericType(valueType))!;
    }

    private sealed class CronPatchConverter<T> : JsonConverter<CronPatch<T>>
    {
        // Reached only when the property is PRESENT, which is precisely what makes IsSet
        // trustworthy. A present-but-null property still lands here and yields a set patch
        // carrying null - the explicit clear.
        public override CronPatch<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => CronPatch<T>.Set(JsonSerializer.Deserialize<T>(ref reader, options));

        public override void Write(Utf8JsonWriter writer, CronPatch<T> value, JsonSerializerOptions options)
        {
            if (!value.IsSet)
            {
                writer.WriteNullValue();
                return;
            }

            JsonSerializer.Serialize(writer, value.Value, options);
        }
    }
}
