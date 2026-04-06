using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Configuration;

namespace BotNexus.Gateway.Configuration;

public sealed class ConfigPathResolver : IConfigPathResolver
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public bool TryGetValue(object config, string path, out object? value, out string error)
    {
        value = null;
        error = string.Empty;

        if (!TryParsePath(path, out var tokens, out error))
            return false;

        object? current = config;
        Type currentType = config.GetType();
        foreach (var token in tokens)
        {
            if (!TryResolveToken(current, currentType, token, createMissing: false, out current, out currentType, out _, out error))
                return false;
        }

        value = current;
        return true;
    }

    public bool TrySetValue(object config, string path, object? value, out string error)
    {
        error = string.Empty;
        if (!TryParsePath(path, out var tokens, out error))
            return false;

        object? current = config;
        Type currentType = config.GetType();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var isLast = i == tokens.Count - 1;

            if (isLast)
            {
                if (token.Index is not null)
                {
                    var listToken = new PathToken(token.Name, null, token.Raw);
                    if (!TryResolveToken(current, currentType, listToken, createMissing: true, out var resolvedList, out var resolvedListType, out var assignList, out error))
                        return false;

                    if (!TrySetListIndexValue(resolvedList, resolvedListType, token.Index.Value, value, out var updatedList, out error))
                        return false;

                    assignList?.Invoke(updatedList);
                    return true;
                }

                if (!TryResolveToken(current, currentType, token, createMissing: true, out var resolved, out var resolvedType, out var assign, out error))
                    return false;

                if (assign is null)
                {
                    error = $"Path '{path}' is not writable.";
                    return false;
                }

                if (!TryConvertValue(value, resolvedType, out var converted, out error))
                    return false;

                assign(converted);
                return true;
            }

            if (!TryResolveToken(current, currentType, token, createMissing: true, out current, out currentType, out var assignIntermediate, out error))
                return false;

            assignIntermediate?.Invoke(current);
        }

        error = $"Unable to set path '{path}'.";
        return false;
    }

    public IReadOnlyList<string> GetAvailablePaths(object config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WalkPaths(config, config.GetType(), string.Empty, paths, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void WalkPaths(
        object? value,
        Type declaredType,
        string path,
        ISet<string> paths,
        ISet<object> visited)
    {
        var effectiveType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (value is null)
        {
            if (!string.IsNullOrWhiteSpace(path))
                paths.Add(path);
            return;
        }

        if (!effectiveType.IsValueType && !effectiveType.IsPrimitive && value is not string)
        {
            if (!visited.Add(value))
                return;
        }

        if (IsLeafType(effectiveType))
        {
            if (!string.IsNullOrWhiteSpace(path))
                paths.Add(path);
            return;
        }

        if (value is IDictionary dictionary)
        {
            if (!string.IsNullOrWhiteSpace(path))
                paths.Add(path);

            var valueType = TryGetDictionaryValueType(effectiveType, out var detectedValueType)
                ? detectedValueType
                : typeof(object);

            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not string key)
                    continue;

                var childPath = string.IsNullOrWhiteSpace(path) ? key : $"{path}.{key}";
                WalkPaths(entry.Value, valueType, childPath, paths, visited);
            }

            return;
        }

        if (value is IList list && TryGetListElementType(effectiveType, out var elementType))
        {
            if (!string.IsNullOrWhiteSpace(path))
                paths.Add(path);

            for (var i = 0; i < list.Count; i++)
            {
                WalkPaths(list[i], elementType, $"{path}[{i}]", paths, visited);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(path))
            paths.Add(path);

        foreach (var property in effectiveType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            var propertyPath = string.IsNullOrWhiteSpace(path)
                ? ToCamelCase(property.Name)
                : $"{path}.{ToCamelCase(property.Name)}";

            WalkPaths(property.GetValue(value), property.PropertyType, propertyPath, paths, visited);
        }
    }

    private static bool TrySetListIndexValue(object? resolvedValue, Type resolvedType, int index, object? value, out object? updatedList, out string error)
    {
        updatedList = resolvedValue;
        error = string.Empty;
        if (index < 0)
        {
            error = $"Index {index} must be >= 0.";
            return false;
        }

        if (!TryGetListElementType(resolvedType, out var elementType))
        {
            error = $"Path segment does not support indexing on '{resolvedType.Name}'.";
            return false;
        }

        if (resolvedValue is null)
        {
            if (!TryCreateListInstance(resolvedType, out resolvedValue, out error))
                return false;

            updatedList = resolvedValue;
        }

        if (resolvedValue is not IList list)
        {
            error = $"Path segment does not resolve to a list for index access.";
            return false;
        }

        if (!TryConvertValue(value, elementType, out var converted, out error))
            return false;

        if (index < list.Count)
        {
            list[index] = converted;
            return true;
        }

        if (index == list.Count)
        {
            list.Add(converted);
            return true;
        }

        error = $"Index {index} is out of range. Current list size is {list.Count}.";
        return false;
    }

    private static bool TryResolveToken(
        object? current,
        Type currentType,
        PathToken token,
        bool createMissing,
        out object? resolvedValue,
        out Type resolvedType,
        out Action<object?>? assign,
        out string error)
    {
        error = string.Empty;
        assign = null;
        resolvedValue = current;
        resolvedType = currentType;

        if (current is null)
        {
            error = $"Path '{token.Raw}' cannot be resolved because a parent value is null.";
            return false;
        }

        object? child = current;
        Type childType = currentType;
        Action<object?>? childAssign = null;

        if (!string.IsNullOrWhiteSpace(token.Name))
        {
            if (TryGetDictionaryValueType(currentType, out var dictionaryValueType))
            {
                if (current is not IDictionary dictionary)
                {
                    error = $"Expected dictionary while resolving '{token.Name}'.";
                    return false;
                }

                var key = FindOrCreateDictionaryKey(dictionary, token.Name);
                var existing = dictionary[key];
                if (existing is null && createMissing && !token.IsTerminalListOnly)
                {
                    if (!TryCreateInstance(dictionaryValueType, out existing, out error))
                        return false;

                    dictionary[key] = existing;
                }

                child = existing;
                childType = dictionaryValueType;
                childAssign = value => dictionary[key] = value;
            }
            else
            {
                var property = FindProperty(currentType, token.Name);
                if (property is null)
                {
                    error = $"Property '{token.Name}' does not exist on '{currentType.Name}'.";
                    return false;
                }

                if (!property.CanRead)
                {
                    error = $"Property '{property.Name}' is not readable.";
                    return false;
                }

                var existing = property.GetValue(current);
                if (existing is null && createMissing && !token.IsTerminalListOnly)
                {
                    if (!TryCreateInstance(property.PropertyType, out existing, out error))
                        return false;

                    if (!property.CanWrite)
                    {
                        error = $"Property '{property.Name}' is read-only.";
                        return false;
                    }

                    property.SetValue(current, existing);
                }

                child = existing;
                childType = property.PropertyType;
                childAssign = property.CanWrite ? value => property.SetValue(current, value) : null;
            }
        }

        if (token.Index is not null)
        {
            if (!TryGetListElementType(childType, out var elementType))
            {
                error = $"Segment '{token.Raw}' cannot be indexed because '{childType.Name}' is not a list.";
                return false;
            }

            if (child is null)
            {
                if (!createMissing)
                {
                    error = $"Segment '{token.Raw}' is null.";
                    return false;
                }

                if (!TryCreateListInstance(childType, out child, out error))
                    return false;
                childAssign?.Invoke(child);
            }

            if (child is not IList list)
            {
                error = $"Segment '{token.Raw}' resolved to a non-list value.";
                return false;
            }

            if (token.Index.Value < 0)
            {
                error = $"Index {token.Index.Value} must be >= 0.";
                return false;
            }

            if (token.Index.Value >= list.Count)
            {
                if (!createMissing)
                {
                    error = $"Index {token.Index.Value} is out of range for '{token.Raw}'.";
                    return false;
                }

                while (list.Count <= token.Index.Value)
                {
                    if (!TryCreateInstance(elementType, out var elementInstance, out error))
                        return false;
                    list.Add(elementInstance);
                }
            }

            var index = token.Index.Value;
            child = list[index];
            if (child is null && createMissing)
            {
                if (!TryCreateInstance(elementType, out child, out error))
                    return false;
                list[index] = child;
            }

            childType = elementType;
            childAssign = value =>
            {
                while (list.Count <= index)
                    list.Add(null);
                list[index] = value;
            };
        }

        resolvedValue = child;
        resolvedType = childType;
        assign = childAssign;
        return true;
    }

    private static bool TryCreateListInstance(Type targetType, out object? instance, out string error)
    {
        var elementType = typeof(object);
        if (!TryGetListElementType(targetType, out elementType))
        {
            instance = null;
            error = $"Unable to create list instance for '{targetType.Name}'.";
            return false;
        }

        var listType = typeof(List<>).MakeGenericType(elementType);
        instance = Activator.CreateInstance(listType);
        if (instance is null)
        {
            error = $"Unable to instantiate list for '{targetType.Name}'.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryCreateInstance(Type targetType, out object? instance, out string error)
    {
        instance = null;
        error = string.Empty;
        var nonNullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (TryGetDictionaryValueType(nonNullableType, out var dictionaryValueType))
        {
            var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), dictionaryValueType);
            instance = Activator.CreateInstance(dictionaryType, StringComparer.OrdinalIgnoreCase);
            if (instance is not null)
                return true;
        }

        if (TryGetListElementType(nonNullableType, out _))
            return TryCreateListInstance(nonNullableType, out instance, out error);

        if (nonNullableType == typeof(string))
        {
            error = $"Unable to instantiate type '{nonNullableType.Name}' automatically.";
            return false;
        }

        if (nonNullableType.IsAbstract || nonNullableType.IsInterface)
        {
            error = $"Unable to instantiate abstract/interface type '{nonNullableType.Name}'.";
            return false;
        }

        try
        {
            instance = Activator.CreateInstance(nonNullableType);
            if (instance is null)
            {
                error = $"Unable to instantiate {nonNullableType.Name}.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Unable to instantiate {nonNullableType.Name}: {ex.Message}";
            return false;
        }
    }

    private static bool TryConvertValue(object? rawValue, Type targetType, out object? converted, out string error)
    {
        converted = null;
        error = string.Empty;

        var nonNullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        var allowsNull = !nonNullableType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null;
        if (rawValue is null)
        {
            if (allowsNull)
                return true;

            error = $"{targetType.Name} does not allow null.";
            return false;
        }

        if (rawValue is string rawString)
        {
            if (string.Equals(rawString, "null", StringComparison.OrdinalIgnoreCase))
            {
                if (allowsNull)
                    return true;

                error = $"{targetType.Name} does not allow null.";
                return false;
            }

            if (nonNullableType == typeof(string))
            {
                converted = rawString;
                return true;
            }

            if (nonNullableType == typeof(bool))
            {
                if (bool.TryParse(rawString, out var boolValue))
                {
                    converted = boolValue;
                    return true;
                }

                error = $"'{rawString}' is not a valid boolean.";
                return false;
            }

            if (nonNullableType.IsEnum)
            {
                if (Enum.TryParse(nonNullableType, rawString, ignoreCase: true, out var enumValue))
                {
                    converted = enumValue;
                    return true;
                }

                error = $"'{rawString}' is not a valid {nonNullableType.Name} value.";
                return false;
            }

            try
            {
                if (nonNullableType == typeof(int))
                    converted = int.Parse(rawString, CultureInfo.InvariantCulture);
                else if (nonNullableType == typeof(long))
                    converted = long.Parse(rawString, CultureInfo.InvariantCulture);
                else if (nonNullableType == typeof(double))
                    converted = double.Parse(rawString, CultureInfo.InvariantCulture);
                else if (nonNullableType == typeof(float))
                    converted = float.Parse(rawString, CultureInfo.InvariantCulture);
                else if (nonNullableType == typeof(decimal))
                    converted = decimal.Parse(rawString, CultureInfo.InvariantCulture);
                else
                    converted = JsonSerializer.Deserialize(rawString, nonNullableType, ReadJsonOptions);

                return true;
            }
            catch (Exception ex)
            {
                error = $"Unable to convert '{rawString}' to {nonNullableType.Name}: {ex.Message}";
                return false;
            }
        }

        if (rawValue is JsonElement element)
        {
            try
            {
                converted = element.Deserialize(nonNullableType, ReadJsonOptions);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Unable to convert JSON element to {nonNullableType.Name}: {ex.Message}";
                return false;
            }
        }

        if (nonNullableType.IsInstanceOfType(rawValue))
        {
            converted = rawValue;
            return true;
        }

        try
        {
            converted = Convert.ChangeType(rawValue, nonNullableType, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Unable to convert value to {nonNullableType.Name}: {ex.Message}";
            return false;
        }
    }

    private static object FindOrCreateDictionaryKey(IDictionary dictionary, string key)
    {
        if (TryFindDictionaryLookupKey(dictionary, key, out var matchedKey) && matchedKey is not null)
            return matchedKey;

        return key;
    }

    private static bool TryFindDictionaryLookupKey(IDictionary dictionary, string key, out object? matchedKey)
    {
        if (dictionary.Contains(key))
        {
            matchedKey = key;
            return true;
        }

        foreach (var existingKey in dictionary.Keys)
        {
            if (existingKey is string existingString &&
                string.Equals(existingString, key, StringComparison.OrdinalIgnoreCase))
            {
                matchedKey = existingKey;
                return true;
            }
        }

        matchedKey = null;
        return false;
    }

    private static PropertyInfo? FindProperty(Type type, string segment)
    {
        return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(property =>
                property.Name.Equals(segment, StringComparison.OrdinalIgnoreCase) ||
                ToCamelCase(property.Name).Equals(segment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetDictionaryValueType(Type type, out Type valueType)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target.IsGenericType &&
            target.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
            target.GetGenericArguments()[0] == typeof(string))
        {
            valueType = target.GetGenericArguments()[1];
            return true;
        }

        foreach (var implemented in target.GetInterfaces())
        {
            if (!implemented.IsGenericType || implemented.GetGenericTypeDefinition() != typeof(IDictionary<,>))
                continue;

            var args = implemented.GetGenericArguments();
            if (args[0] != typeof(string))
                continue;

            valueType = args[1];
            return true;
        }

        valueType = typeof(object);
        return false;
    }

    private static bool TryGetListElementType(Type type, out Type elementType)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target.IsArray)
        {
            elementType = target.GetElementType() ?? typeof(object);
            return true;
        }

        if (target.IsGenericType &&
            target.GetGenericTypeDefinition() == typeof(List<>))
        {
            elementType = target.GetGenericArguments()[0];
            return true;
        }

        foreach (var implemented in target.GetInterfaces())
        {
            if (!implemented.IsGenericType || implemented.GetGenericTypeDefinition() != typeof(IList<>))
                continue;

            elementType = implemented.GetGenericArguments()[0];
            return true;
        }

        elementType = typeof(object);
        return false;
    }

    private static string ToCamelCase(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        if (value.Length == 1)
            return value.ToLowerInvariant();

        return char.ToLowerInvariant(value[0]) + value[1..];
    }

    private static bool IsLeafType(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        return target.IsPrimitive
               || target.IsEnum
               || target == typeof(string)
               || target == typeof(decimal)
               || target == typeof(DateTime)
               || target == typeof(DateTimeOffset)
               || target == typeof(TimeSpan)
               || target == typeof(Guid)
               || target == typeof(Uri);
    }

    private static bool TryParsePath(string? path, out IReadOnlyList<PathToken> tokens, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            tokens = [];
            error = "Key path is required.";
            return false;
        }

        var rawSegments = SplitPath(path);
        var parsed = new List<PathToken>(rawSegments.Count);
        foreach (var segment in rawSegments)
        {
            if (!TryParseSegment(segment, out var token, out error))
            {
                tokens = [];
                return false;
            }

            parsed.Add(token);
        }

        tokens = parsed;
        return true;
    }

    private static List<string> SplitPath(string path)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        var bracketDepth = 0;
        foreach (var ch in path.Trim())
        {
            if (ch == '.' && bracketDepth == 0)
            {
                if (current.Length > 0)
                {
                    segments.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            if (ch == '[')
                bracketDepth++;
            else if (ch == ']')
                bracketDepth = Math.Max(0, bracketDepth - 1);

            current.Append(ch);
        }

        if (current.Length > 0)
            segments.Add(current.ToString());

        return segments;
    }

    private static bool TryParseSegment(string segment, out PathToken token, out string error)
    {
        token = default;
        error = string.Empty;
        var trimmed = segment.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = "Key path contains an empty segment.";
            return false;
        }

        var bracketStart = trimmed.IndexOf('[');
        if (bracketStart < 0)
        {
            token = new PathToken(trimmed, null, trimmed);
            return true;
        }

        var bracketEnd = trimmed.IndexOf(']', bracketStart + 1);
        if (bracketEnd < 0 || bracketEnd != trimmed.Length - 1)
        {
            error = $"Invalid segment '{trimmed}'. Use 'name[index]' format.";
            return false;
        }

        var name = trimmed[..bracketStart];
        var indexText = trimmed[(bracketStart + 1)..bracketEnd];
        if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            error = $"Invalid index '{indexText}' in segment '{trimmed}'.";
            return false;
        }

        token = new PathToken(name, index, trimmed);
        return true;
    }

    private readonly record struct PathToken(string Name, int? Index, string Raw)
    {
        public bool IsTerminalListOnly => string.IsNullOrWhiteSpace(Name) && Index is not null;
    }
}
