using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Commands.Doctor;

/// <summary>
/// Reads and writes doctor-check values by dotted configuration path, refusing any path that the
/// typed <see cref="PlatformConfig"/> graph does not bind.
/// <para>
/// This exists because #2764: two compaction checks hand-rolled their own notion of where the
/// setting lived (a root-level <c>compaction</c> block) while the binder actually reads
/// <c>gateway.compaction</c>. The duplicated knowledge was wrong, so the read was permanently
/// null and the write produced an inert block nothing binds. <see cref="IConfigPathResolver"/>
/// already owns the authoritative answer to "does this path exist in configuration?", so checks
/// ask it rather than restating the shape. A path that nothing binds cannot be read here and
/// throws on write - the class of defect becomes unrepresentable rather than merely fixed.
/// </para>
/// </summary>
internal static class BoundConfigPath
{
    private static readonly IConfigPathResolver Resolver = new ConfigPathResolver();

    /// <summary>
    /// Reads a string value from the raw config document, or returns false when the path is
    /// absent from the document. Throws when the path is not bound by <see cref="PlatformConfig"/>
    /// at all, since that indicates a check is reading somewhere the gateway never looks.
    /// </summary>
    public static bool TryReadString(JsonObject root, string path, out string? value)
    {
        value = null;
        if (!IsBound(path))
            return false;

        JsonNode? current = root;
        foreach (var segment in Split(path))
        {
            if (current is not JsonObject obj || !TryGetChild(obj, segment, out current))
                return false;
        }

        if (current is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text))
            return false;

        value = text;
        return true;
    }

    /// <summary>
    /// Writes a string value into the raw config document at <paramref name="path"/>, creating
    /// intermediate objects as needed. Throws <see cref="InvalidOperationException"/> when the
    /// path is not bound by <see cref="PlatformConfig"/>, so a fix can never persist a block the
    /// gateway will silently ignore.
    /// </summary>
    public static void WriteString(JsonObject root, string path, string value)
    {
        if (!IsBound(path))
        {
            throw new InvalidOperationException(
                $"Configuration path '{path}' is not bound by PlatformConfig; writing it would produce an inert config block.");
        }

        var segments = Split(path);
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!TryGetChild(current, segments[i], out var child) || child is not JsonObject childObject)
            {
                childObject = new JsonObject();
                current[segments[i]] = childObject;
            }

            current = childObject;
        }

        SetLeaf(current, segments[^1], value);
    }

    /// <summary>
    /// Asks the resolver whether the path exists on a throwaway <see cref="PlatformConfig"/>
    /// graph. The probe instance is discarded, so any nodes the resolver materialises while
    /// walking are irrelevant - only the yes/no answer is used.
    /// </summary>
    private static bool IsBound(string path)
        => Resolver.TrySetValue(new PlatformConfig(), path, null, out _);

    private static string[] Split(string path)
        => path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // config.json keys are conventionally camelCase but operators hand-edit the file, so match
    // an existing key case-insensitively rather than silently creating a duplicate sibling.
    private static bool TryGetChild(JsonObject obj, string name, out JsonNode? child)
    {
        foreach (var kvp in obj)
        {
            if (string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                child = kvp.Value;
                return true;
            }
        }

        child = null;
        return false;
    }

    private static void SetLeaf(JsonObject obj, string name, string value)
    {
        foreach (var kvp in obj)
        {
            if (string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                obj[kvp.Key] = value;
                return;
            }
        }

        obj[name] = value;
    }
}
