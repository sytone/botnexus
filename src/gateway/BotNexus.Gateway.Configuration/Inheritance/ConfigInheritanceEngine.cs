using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration.Shadow;

namespace BotNexus.Gateway.Configuration.Inheritance;

/// <summary>
/// Supplies the inheritance policy for a dotted configuration path.
/// </summary>
/// <remarks>
/// An interface rather than a direct <see cref="ConfigInheritanceRegistry"/> call so the engine can be
/// tested against a synthetic policy map without standing up a whole annotated CLR graph, and so a
/// caller migrating a domain (#2426) can bridge its own path-to-property mapping without the engine
/// needing to know the domain's CLR shape.
/// </remarks>
public interface IConfigPolicyResolver
{
    /// <summary>
    /// Returns the declared policy for <paramref name="path"/>, or <see langword="null"/> when the path
    /// carries no classification.
    /// </summary>
    /// <remarks>
    /// Returning null rather than a default is deliberate: the engine treats an unclassified path as an
    /// error condition to surface, not as an implied <see cref="ConfigInheritancePolicy.ScalarOverride"/>.
    /// A silently-defaulted policy is indistinguishable from a considered one, which is the exact drift
    /// #2137 documents.
    /// </remarks>
    ConfigInheritancePolicy? GetPolicy(string path);
}

/// <summary>
/// A policy resolver backed by an explicit path-to-policy map.
/// </summary>
/// <remarks>
/// Paths are matched most-specific-first, so a caller can classify <c>heartbeat</c> as
/// <see cref="ConfigInheritancePolicy.DeepMerge"/> and still pin <c>heartbeat.quietHours</c> to
/// <see cref="ConfigInheritancePolicy.ReplaceAsUnit"/> beneath it.
/// </remarks>
public sealed class MapConfigPolicyResolver : IConfigPolicyResolver
{
    private readonly IReadOnlyDictionary<string, ConfigInheritancePolicy> _map;

    /// <summary>Creates a resolver over an explicit path-to-policy map.</summary>
    public MapConfigPolicyResolver(IReadOnlyDictionary<string, ConfigInheritancePolicy> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        _map = map;
    }

    /// <inheritdoc />
    public ConfigInheritancePolicy? GetPolicy(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (_map.TryGetValue(path, out var exact))
            return exact;

        // Walk up the path so a policy declared on a parent object governs its unclassified children.
        // Without this an author would have to restate DeepMerge on every leaf of a merged block.
        var current = path;
        while (true)
        {
            var cut = current.LastIndexOf('.');
            if (cut < 0)
                return null;

            current = current[..cut];
            if (_map.TryGetValue(current, out var ancestor))
            {
                // ReplaceAsUnit and KeyedMerge are decided at the node that declares them; a descendant
                // of a replace-as-unit block is never independently merged, so it inherits nothing here.
                return ancestor;
            }
        }
    }
}

/// <summary>
/// Overlays a stack of configuration layers according to each property's declared inheritance policy,
/// preserving the tri-state presence distinction and recording per-property provenance (#2425).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one engine.</b> Every configuration domain currently implements merge by hand, so the
/// semantics of <c>null</c>, <c>false</c>, <c>0</c>, <c>""</c>, <c>[]</c> and <c>{}</c> differ per
/// domain and are mostly untested (#2137). Those differences are invisible until an operator's value
/// silently fails to apply. Executing one engine against declared policies makes the semantics uniform
/// and testable in a single place.
/// </para>
/// <para>
/// <b>Why it operates on raw documents.</b> Presence must be read from the document, because a bound
/// POCO has already collapsed absent and explicit-null into the same null field. A merge built on
/// bound objects cannot distinguish "inherit this" from "suppress this" no matter how carefully it is
/// written.
/// </para>
/// <para>
/// <b>What it deliberately does not do.</b> It does not write to any input layer, and it does not
/// materialise a value no layer supplied. Both would freeze a child against a default it should have
/// kept tracking, which is the persistence defect #2429 addresses and the mechanism by which an
/// inherited secret would be copied into a child block (AC7).
/// </para>
/// </remarks>
public sealed class ConfigInheritanceEngine
{
    private readonly IConfigPolicyResolver _policies;
    private readonly ConfigInheritancePolicy _defaultPolicy;

    /// <summary>
    /// Creates an engine that resolves policies through <paramref name="policies"/>.
    /// </summary>
    /// <param name="policies">Supplies the declared policy for each path.</param>
    /// <param name="defaultPolicy">
    /// Policy applied to a path the resolver does not classify. Defaults to
    /// <see cref="ConfigInheritancePolicy.ScalarOverride"/>, which is the least surprising behaviour for
    /// a leaf; the architecture fitness test from #2424 - not this fallback - is what prevents a real
    /// property from going unclassified.
    /// </param>
    public ConfigInheritanceEngine(
        IConfigPolicyResolver policies,
        ConfigInheritancePolicy defaultPolicy = ConfigInheritancePolicy.ScalarOverride)
    {
        ArgumentNullException.ThrowIfNull(policies);
        _policies = policies;
        _defaultPolicy = defaultPolicy;
    }

    /// <summary>
    /// Overlays <paramref name="layers"/> in order, lowest precedence first, and returns the merged
    /// document with per-property provenance.
    /// </summary>
    /// <param name="layers">
    /// The layer stack. The first entry is the base (for example <c>agents.defaults</c>); each
    /// subsequent layer overrides it according to policy.
    /// </param>
    public ConfigOverlayResult Overlay(IReadOnlyList<ConfigLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        var result = new JsonObject();
        var provenance = new Dictionary<string, ConfigProvenance>(StringComparer.Ordinal);

        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (layer.Document is null)
                continue;

            var isTopLayer = i == layers.Count - 1;
            ApplyLayer(layer, layer.Document, result, prefix: string.Empty, provenance, isTopLayer);
        }

        return new ConfigOverlayResult(result, provenance);
    }

    private void ApplyLayer(
        ConfigLayer layer,
        JsonObject source,
        JsonObject target,
        string prefix,
        Dictionary<string, ConfigProvenance> provenance,
        bool isTopLayer)
    {
        foreach (var (key, node) in source)
        {
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            var policy = _policies.GetPolicy(path) ?? _defaultPolicy;

            switch (policy)
            {
                case ConfigInheritancePolicy.LocalOnly:
                case ConfigInheritancePolicy.RuntimeOnly:
                    // Declared not to participate in layering, so only the owning (highest-precedence)
                    // layer may supply a value. A lower layer's value is discarded rather than
                    // inherited: an operator who sets a display name or emoji at the shared defaults
                    // layer has made a mistake, and honouring it would hand every agent the same
                    // identity - a silent, uniform, and very confusing outcome.
                    if (isTopLayer)
                        Assign(target, key, node, path, layer, policy, provenance);

                    break;

                case ConfigInheritancePolicy.DeepMerge when node is JsonObject nested && nested.Count > 0:
                    // Merge field-by-field so a child setting one member of a block inherits the rest -
                    // the behaviour operators expect from heartbeat (#2137, #2423).
                    var child = target[key] as JsonObject;
                    if (child is null)
                    {
                        child = new JsonObject();
                        target[key] = child;
                    }

                    ApplyLayer(layer, nested, child, path, provenance, isTopLayer);
                    break;

                case ConfigInheritancePolicy.KeyedMerge when node is JsonObject keyed && keyed.Count > 0:
                    // Keys present only in a lower layer survive; keys the child sets win. Each key is an
                    // independently-owned subtree, so the child may address one entry without restating
                    // the others.
                    var bucket = target[key] as JsonObject;
                    if (bucket is null)
                    {
                        bucket = new JsonObject();
                        target[key] = bucket;
                    }

                    foreach (var (entryKey, entryNode) in keyed)
                    {
                        var entryPath = $"{path}.{entryKey}";
                        Assign(bucket, entryKey, entryNode, entryPath, layer, policy, provenance);
                    }

                    break;

                default:
                    // ScalarOverride, ReplaceAsUnit, Custom, and the empty-object/array forms of the
                    // merge policies: the child's value replaces the parent's wholesale.
                    Assign(target, key, node, path, layer, policy, provenance);
                    break;
            }
        }
    }

    private static void Assign(
        JsonObject target,
        string key,
        JsonNode? node,
        string path,
        ConfigLayer layer,
        ConfigInheritancePolicy policy,
        Dictionary<string, ConfigProvenance> provenance)
    {
        // DeepCopy so the result never aliases an input layer's nodes. Without it, a later mutation of
        // the merged document would reach back and modify agents.defaults for every other agent.
        target[key] = node?.DeepClone();

        provenance[path] = new ConfigProvenance(
            path,
            layer.Name,
            policy,
            node is null ? ConfigValueState.ExplicitNull : ConfigValueState.Value);
    }
}
