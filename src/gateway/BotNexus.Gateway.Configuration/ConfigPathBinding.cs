using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Answers "is this dotted path a place configuration actually lives?" from the declared
/// <see cref="PlatformConfig"/> <em>type</em> graph, without needing an instance.
/// </summary>
/// <remarks>
/// <para>
/// #2887. The canonical-path surface must be able to refuse a path nothing binds, because the
/// alternative - returning <see langword="null"/> - is exactly the #2764 defect: a wrong traversal
/// and an unset value are indistinguishable, so a check reading the wrong place reports a healthy
/// platform as broken (or, worse, reads as a clean pass while being structurally incapable of
/// firing).
/// </para>
/// <para>
/// Recognition walks <em>types</em>, not values. An instance-based probe cannot answer the question
/// for a dictionary whose value type is an opaque JSON leaf: materialising
/// <c>gateway.extensions.defaults["botnexus-skills"]</c> yields a <see cref="JsonElement"/> whose
/// own children are free-form, so the walk would hit a null parent and report a legitimate path as
/// unrecognised. Walking types lets the recogniser stop at the opaque boundary and accept whatever
/// follows, which is the correct answer: past that point the platform genuinely does not model the
/// shape.
/// </para>
/// </remarks>
public static class ConfigPathBinding
{
    /// <summary>
    /// Returns true when <paramref name="path"/> addresses a location the typed configuration graph
    /// models (or a free-form key beneath an opaque JSON node). On failure <paramref name="error"/>
    /// names the offending segment, so a caller can surface an explicit failure rather than an
    /// ambiguous null.
    /// </summary>
    public static bool TryRecognise(string? path, out string error)
        => TryRecognise(path, out _, out error);

    /// <summary>
    /// As <see cref="TryRecognise(string?, out string)"/>, additionally reporting the declared type
    /// the path resolves to. <paramref name="declaredType"/> is <see langword="null"/> when the path
    /// terminates inside an opaque JSON node, where no declared type exists.
    /// </summary>
    public static bool TryRecognise(string? path, out Type? declaredType, out string error)
    {
        declaredType = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Key path is required.";
            return false;
        }

        if (!ConfigPathSyntax.TryValidateBrackets(path, out error))
            return false;

        var segments = SplitSegments(path);
        if (segments.Count == 0)
        {
            error = "Key path is required.";
            return false;
        }

        Type current = typeof(PlatformConfig);
        foreach (var raw in segments)
        {
            if (!TryParseSegment(raw, out var name, out var index, out error))
                return false;

            if (IsOpaque(current))
            {
                // Free-form JSON from here down: the platform deliberately does not model the
                // shape (extension settings, feature-flag filter objects, agent metadata), so any
                // key beneath it is legitimate and no declared type can be reported.
                declaredType = null;
                return true;
            }

            if (name.Length > 0)
            {
                if (!TryResolveMember(current, name, path, out current, out error))
                    return false;
            }

            if (index is not null)
            {
                if (!TryGetElementType(current, out var elementType))
                {
                    error = $"Configuration path '{path}' is not recognised: '{raw}' cannot be indexed because "
                            + $"'{FriendlyName(current)}' is not a list.";
                    return false;
                }

                current = elementType;
            }
        }

        declaredType = IsOpaque(current) ? null : current;
        return true;
    }

    private static bool TryResolveMember(Type owner, string name, string path, out Type resolved, out string error)
    {
        error = string.Empty;

        if (TryGetDictionaryValueType(owner, out var valueType))
        {
            // Any key is addressable in a string-keyed dictionary; the value type is what matters.
            resolved = valueType;
            return true;
        }

        var property = FindProperty(owner, name);
        if (property is null)
        {
            resolved = owner;
            error = $"Configuration path '{path}' is not recognised: '{name}' is not a configuration property of "
                    + $"'{FriendlyName(owner)}'. Reading or writing it would address a location the gateway never "
                    + "looks at.";
            return false;
        }

        resolved = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        return true;
    }

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            var jsonName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
            if (jsonName is not null && jsonName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property;

            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property;

            if (ToCamelCase(property.Name).Equals(name, StringComparison.OrdinalIgnoreCase))
                return property;
        }

        return null;
    }

    /// <summary>
    /// True for nodes whose children configuration deliberately does not model. Anything below one
    /// of these is free-form and therefore always recognised.
    /// </summary>
    private static bool IsOpaque(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        return target == typeof(JsonElement)
               || target == typeof(object)
               || typeof(JsonNode).IsAssignableFrom(target)
               || typeof(JsonDocument).IsAssignableFrom(target);
    }

    private static bool TryGetDictionaryValueType(Type type, out Type valueType)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        foreach (var candidate in Enumerate(target))
        {
            if (!candidate.IsGenericType)
                continue;

            var definition = candidate.GetGenericTypeDefinition();
            if (definition != typeof(Dictionary<,>)
                && definition != typeof(IDictionary<,>)
                && definition != typeof(IReadOnlyDictionary<,>))
                continue;

            var args = candidate.GetGenericArguments();
            if (args[0] != typeof(string))
                continue;

            valueType = Nullable.GetUnderlyingType(args[1]) ?? args[1];
            return true;
        }

        valueType = typeof(object);
        return false;
    }

    private static bool TryGetElementType(Type type, out Type elementType)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target == typeof(string))
        {
            elementType = typeof(object);
            return false;
        }

        if (target.IsArray)
        {
            elementType = target.GetElementType() ?? typeof(object);
            return true;
        }

        foreach (var candidate in Enumerate(target))
        {
            if (!candidate.IsGenericType)
                continue;

            var definition = candidate.GetGenericTypeDefinition();
            if (definition != typeof(List<>)
                && definition != typeof(IList<>)
                && definition != typeof(IReadOnlyList<>)
                && definition != typeof(IEnumerable<>))
                continue;

            elementType = candidate.GetGenericArguments()[0];
            return true;
        }

        elementType = typeof(object);
        return false;
    }

    private static IEnumerable<Type> Enumerate(Type target)
    {
        yield return target;
        foreach (var implemented in target.GetInterfaces())
            yield return implemented;
    }

    private static string FriendlyName(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (!target.IsGenericType)
            return target.Name;

        var name = target.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick > 0)
            name = name[..tick];

        return $"{name}<{string.Join(", ", target.GetGenericArguments().Select(FriendlyName))}>";
    }

    private static string ToCamelCase(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return value.Length == 1
            ? value.ToLowerInvariant()
            : char.ToLowerInvariant(value[0]) + value[1..];
    }

    private static bool TryParseSegment(string raw, out string name, out int? index, out string error)
    {
        error = string.Empty;
        index = null;
        name = raw.Trim();

        if (name.Length == 0)
        {
            error = "Key path contains an empty segment.";
            return false;
        }

        var bracket = name.IndexOf('[', StringComparison.Ordinal);
        if (bracket < 0)
            return true;

        var close = name.IndexOf(']', bracket + 1);
        if (close != name.Length - 1)
        {
            error = $"Invalid segment '{name}'. Use 'name[index]' format.";
            return false;
        }

        var indexText = name[(bracket + 1)..close];
        if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            error = $"Invalid index '{indexText}' in segment '{name}'.";
            return false;
        }

        index = parsed;
        name = name[..bracket];
        return true;
    }

    // Splits on '.' at bracket depth zero, matching the raw and typed path splitters exactly so a
    // path recognised here is the same path they will walk.
    private static List<string> SplitSegments(string path)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0;

        foreach (var ch in path.Trim())
        {
            if (ch == '.' && depth == 0)
            {
                if (current.Length > 0)
                {
                    segments.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            if (ch == '[')
                depth++;
            else if (ch == ']')
                depth = Math.Max(0, depth - 1);

            current.Append(ch);
        }

        if (current.Length > 0)
            segments.Add(current.ToString());

        return segments;
    }
}
