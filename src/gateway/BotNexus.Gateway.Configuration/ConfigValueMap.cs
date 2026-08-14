using System.Collections;
using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// An ordered set of named configuration values a caller wants written, expressed without naming
/// any JSON node type.
/// </summary>
/// <remarks>
/// <para>
/// #2887. Consumers outside the configuration project used to build <see cref="JsonObject"/>
/// payloads and index them into the config document by hand. Handing them a node type is what made
/// hand-rolled traversal expressible at all, so the payload type is part of the closed surface:
/// a caller states <em>values</em>, and the configuration project alone decides how they are
/// represented on disk.
/// </para>
/// <para>
/// Values may be <see langword="null"/>, a string, a bool, an integral or floating-point number, a
/// sequence of strings, or a nested <see cref="ConfigValueMap"/>. Anything else is rejected at write
/// time with an explicit error rather than being silently stringified.
/// </para>
/// </remarks>
public sealed class ConfigValueMap : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly List<KeyValuePair<string, object?>> _entries = [];

    /// <summary>Adds or replaces <paramref name="name"/>, preserving first-insertion order.</summary>
    public ConfigValueMap Set(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        for (var i = 0; i < _entries.Count; i++)
        {
            if (string.Equals(_entries[i].Key, name, StringComparison.OrdinalIgnoreCase))
            {
                _entries[i] = new KeyValuePair<string, object?>(_entries[i].Key, value);
                return this;
            }
        }

        _entries.Add(new KeyValuePair<string, object?>(name, value));
        return this;
    }

    /// <summary>Adds <paramref name="name"/> only when <paramref name="value"/> is not null.</summary>
    public ConfigValueMap SetIfNotNull(string name, object? value)
        => value is null ? this : Set(name, value);

    /// <summary>The number of named values.</summary>
    public int Count => _entries.Count;

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
