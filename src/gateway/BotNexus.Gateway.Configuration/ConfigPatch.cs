using System.Globalization;
using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// A single addressed mutation within a configuration patch batch: set the value at
/// <see cref="Path"/>, or remove the node it addresses.
/// </summary>
/// <remarks>
/// <para>
/// Issue #2059. The settings UI previously saved by PUTting <em>every materialised top-level
/// section</em> of the effective config snapshot it loaded. That is a whole-aggregate write
/// disguised as a field edit: changing one number in <c>gateway</c> also rewrote <c>providers</c>
/// and <c>channels</c> with values read minutes earlier, so any concurrent edit to an unrelated
/// section was silently reverted. A patch addresses only what the operator actually touched, so a
/// section nobody edited is never written and therefore can never be clobbered.
/// </para>
/// <para>
/// <see cref="Path"/> uses the same addressing the settings renderer already produces for every
/// field it draws (<c>a.b.c</c> for object members, <c>a.b[0]</c> for array elements), so the UI
/// does not have to derive a second path dialect to describe its own edits.
/// </para>
/// </remarks>
/// <param name="Path">Dotted path with optional <c>[index]</c> array segments, e.g. <c>gateway.port</c>.</param>
/// <param name="Value">The value to write. Ignored when <paramref name="Remove"/> is true.</param>
/// <param name="Remove">When true, the node at <paramref name="Path"/> is removed instead of set.</param>
public sealed record ConfigPatchOperation(string Path, JsonNode? Value = null, bool Remove = false);

/// <summary>
/// Applies <see cref="ConfigPatchOperation"/> batches to a raw configuration document.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="PlatformConfigWriter"/> so the addressing rules are unit-testable
/// without touching a file, and so the writer keeps exactly one write pipeline
/// (<c>MutateCoreAsync</c>) rather than growing a second one for patches.
/// </remarks>
public static class ConfigPatchApplier
{
    /// <summary>
    /// Applies every operation to <paramref name="root"/> in order.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when all operations applied, otherwise a caller-presentable message
    /// describing the first operation that could not be applied. The caller is expected to abort
    /// the whole batch on a non-null result: a patch presented as one Save must not half-commit.
    /// </returns>
    public static string? Apply(JsonObject root, IReadOnlyList<ConfigPatchOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(operations);

        foreach (var operation in operations)
        {
            var error = ApplyOne(root, operation);
            if (error is not null)
                return error;
        }

        return null;
    }

    /// <summary>
    /// The distinct top-level sections a batch is entitled to empty, for the destructive-section
    /// guard (#2816).
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: only an operation whose path is a <em>bare root segment</em> (i.e. it
    /// replaces or removes the whole section) declares that section. An edit to
    /// <c>gateway.port</c> does NOT entitle the batch to flatten <c>gateway</c> - if applying that
    /// edit somehow emptied the section, that is exactly the collateral destruction the guard
    /// exists to refuse, and it should be refused rather than waved through because the path
    /// happened to start with the section's name.
    /// </remarks>
    public static IReadOnlyCollection<string> DeclaredSections(IReadOnlyList<ConfigPatchOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            var segments = Tokenize(operation.Path).ToList();
            if (segments.Count == 1 && !IsIndex(segments[0]))
                declared.Add(segments[0]);
        }

        return declared;
    }

    private static string? ApplyOne(JsonObject root, ConfigPatchOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Path))
            return "A patch operation had an empty path.";

        var segments = Tokenize(operation.Path).ToList();
        if (segments.Count == 0)
            return $"Patch path '{operation.Path}' did not resolve to any segment.";

        JsonNode? current = root;

        // Walk to the parent of the addressed node, materialising intermediate containers as we go.
        // Materialisation is the point of AC "initially absent sections": a field edit under a
        // section that has never existed on disk must be able to bring that section into being,
        // which the old save path could not do (it filtered the save set by the raw document's
        // existing top-level keys, so a default-only section was unreachable forever).
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var segment = segments[i];
            var nextIsIndex = IsIndex(segments[i + 1]);

            if (IsIndex(segment))
            {
                if (current is not JsonArray array)
                    return $"Patch path '{operation.Path}' expects an array at segment '{segment}'.";
                if (!TryIndex(segment, out var index) || index < 0 || index >= array.Count)
                    return $"Patch path '{operation.Path}' addresses out-of-range index '{segment}'.";
                current = array[index];
                continue;
            }

            if (current is not JsonObject obj)
                return $"Patch path '{operation.Path}' expects an object at segment '{segment}'.";

            if (nextIsIndex)
            {
                if (obj[segment] is not JsonArray childArray)
                {
                    childArray = [];
                    obj[segment] = childArray;
                }

                current = childArray;
            }
            else
            {
                if (obj[segment] is not JsonObject childObject)
                {
                    childObject = [];
                    obj[segment] = childObject;
                }

                current = childObject;
            }
        }

        var last = segments[^1];
        if (IsIndex(last))
        {
            if (current is not JsonArray array)
                return $"Patch path '{operation.Path}' expects an array at segment '{last}'.";
            if (!TryIndex(last, out var index) || index < 0 || index >= array.Count)
                return $"Patch path '{operation.Path}' addresses out-of-range index '{last}'.";

            if (operation.Remove)
                array.RemoveAt(index);
            else
                array[index] = operation.Value?.DeepClone();

            return null;
        }

        if (current is not JsonObject parent)
            return $"Patch path '{operation.Path}' expects an object at segment '{last}'.";

        if (operation.Remove)
            parent.Remove(last);
        else
            parent[last] = operation.Value?.DeepClone();

        return null;
    }

    private static bool IsIndex(string segment) => segment.StartsWith('[');

    private static bool TryIndex(string segment, out int index)
        => int.TryParse(segment.Trim('[', ']'), NumberStyles.Integer, CultureInfo.InvariantCulture, out index);

    /// <summary>
    /// Splits a path into object-member and array-index segments. Mirrors the renderer's own
    /// tokenizer so a path the settings form emits addresses the same node here.
    /// </summary>
    public static IEnumerable<string> Tokenize(string path)
    {
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bracket = part.IndexOf('[', StringComparison.Ordinal);
            if (bracket < 0)
            {
                yield return part;
                continue;
            }

            if (bracket > 0)
                yield return part[..bracket];

            var rest = part[bracket..];
            while (rest.Length > 0)
            {
                var close = rest.IndexOf(']', StringComparison.Ordinal);
                if (close < 0)
                    yield break;
                yield return rest[..(close + 1)];
                rest = rest[(close + 1)..];
            }
        }
    }
}

/// <summary>
/// Outcome of an attempted configuration patch.
/// </summary>
/// <param name="Success">Whether the batch was applied and persisted.</param>
/// <param name="Revision">The revision now committed on disk (present on success).</param>
/// <param name="Errors">Rejection messages; empty on success.</param>
public sealed record ConfigPatchResult(bool Success, string? Revision, IReadOnlyList<string> Errors);
