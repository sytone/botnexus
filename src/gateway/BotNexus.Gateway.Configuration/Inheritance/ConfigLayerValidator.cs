using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration.Inheritance;

/// <summary>
/// Validates a layer stack and attributes each failure to the layer that supplied the offending
/// value (#2425 AC9).
/// </summary>
/// <remarks>
/// Validating only the merged result tells an operator that <c>heartbeat.intervalMinutes</c> is
/// invalid but not <em>where to go and fix it</em>. When the value came from a shared defaults layer,
/// the operator inspects their own agent block, finds nothing wrong, and is stuck. Validating with the
/// provenance map in hand converts that into a single actionable edit.
/// </remarks>
public static class ConfigLayerValidator
{
    /// <summary>
    /// Runs <paramref name="rules"/> against the overlaid document and returns one error per failure,
    /// each naming the effective path and the supplying layer.
    /// </summary>
    /// <param name="result">The overlay result, including its provenance map.</param>
    /// <param name="rules">
    /// Path-keyed predicates. A rule returns an error message for an invalid value, or
    /// <see langword="null"/> when the value is acceptable.
    /// </param>
    public static IReadOnlyList<ConfigLayerValidationError> Validate(
        ConfigOverlayResult result,
        IReadOnlyDictionary<string, Func<JsonNode?, string?>> rules)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(rules);

        var errors = new List<ConfigLayerValidationError>();

        foreach (var (path, rule) in rules)
        {
            // A rule for a path no layer supplied is not a failure. Absence is the business of
            // required-field validation, which operates on the effective document as a whole; treating
            // it as an error here would fire on every optional property.
            if (!result.Provenance.TryGetValue(path, out var provenance))
                continue;

            var message = rule(ResolveNode(result.Document, path));
            if (message is null)
                continue;

            errors.Add(new ConfigLayerValidationError(
                path,
                provenance.LayerName ?? "(unknown)",
                message));
        }

        return errors;
    }

    private static JsonNode? ResolveNode(JsonObject root, string path)
    {
        JsonNode? current = root;

        foreach (var segment in path.Split('.'))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out var next))
                return null;

            current = next;
        }

        return current;
    }
}
