using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration.Inheritance;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Merges agent configuration layers by executing <see cref="ConfigInheritanceEngine"/> against the
/// policies declared by <see cref="ConfigInheritanceAttribute"/> (#3485 D2).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this replaces.</b> Four components independently answered "which value wins":
/// <c>AgentConfigMerger</c> (a hand-maintained per-property switch), <c>ExtensionConfigMerger</c>,
/// <c>PlatformConfigPostConfigure</c>, and this engine - which until now had no production caller at
/// all. A hand-maintained property list is the mechanism behind #2423: adding a property to
/// <see cref="AgentDefinitionConfig"/> silently opted it out of inheritance, because nothing failed
/// when the merger was not taught about it.
/// </para>
/// <para>
/// <b>Why the merge happens on documents, not objects.</b> The layering is performed on raw
/// <see cref="JsonObject"/> documents and bound once at the end. Binding first would collapse
/// "absent" and "explicit null" into the same null field, and those mean opposite things - inherit
/// versus suppress. The previous merger worked around this by threading a
/// <see cref="JsonElement"/> alongside every bound object purely to re-read presence; routing through
/// documents removes the need for that parallel channel.
/// </para>
/// <para>
/// <b>Policy source.</b> Policies come from <see cref="ConfigInheritanceRegistry"/>, which reads the
/// <see cref="ConfigInheritanceAttribute"/> on each property. Adding a property therefore requires a
/// deliberate classification decision, enforced by the #2424 fitness test, rather than an edit to a
/// list nobody remembers exists.
/// </para>
/// </remarks>
public static class AgentConfigInheritance
{
    /// <summary>
    /// Serializer options matching how platform config is written, so a bound object round-trips to
    /// the same document shape the file would contain.
    /// </summary>
    private static readonly JsonSerializerOptions DocumentOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The policy resolver for <see cref="AgentDefinitionConfig"/>, built once from the declared
    /// attributes.
    /// </summary>
    /// <remarks>
    /// <see cref="Lazy{T}"/> rather than a plain static field: static initialisers run in textual
    /// order, so a resolver declared above the map it reads would capture an empty dictionary and
    /// classify nothing - the same silent fallback described on <see cref="BuildPolicyMap"/>. Lazy
    /// removes the ordering dependency entirely rather than relying on a comment to preserve it.
    /// </remarks>
    private static readonly Lazy<IConfigPolicyResolver> AgentPolicies =
        new(() => new MapConfigPolicyResolver(BuildPolicyMap()));

    /// <summary>Layer name for world-level agent defaults.</summary>
    internal const string DefaultsLayerName = "agents.defaults";

    /// <summary>Layer name for the per-agent block.</summary>
    internal const string AgentLayerName = "agent";

    /// <summary>
    /// Overlays <paramref name="agentDocument"/> onto <paramref name="defaultsDocument"/> and binds
    /// the result.
    /// </summary>
    /// <param name="defaultsDocument">
    /// The <c>agents.defaults</c> document, or <see langword="null"/> when no defaults exist.
    /// </param>
    /// <param name="agentDocument">The per-agent document.</param>
    /// <returns>The effective configuration, and the provenance of every path.</returns>
    public static AgentInheritanceResult Overlay(JsonObject? defaultsDocument, JsonObject? agentDocument)
    {
        var engine = new ConfigInheritanceEngine(AgentPolicies.Value);

        var result = engine.Overlay(
        [
            new ConfigLayer(DefaultsLayerName, defaultsDocument),
            new ConfigLayer(AgentLayerName, agentDocument),
        ]);

        var effective = result.Document.Deserialize<AgentDefinitionConfig>(DocumentOptions)
                        ?? new AgentDefinitionConfig();

        return new AgentInheritanceResult(effective, result);
    }

    /// <summary>
    /// Converts a bound configuration object back to its document form.
    /// </summary>
    /// <remarks>
    /// Lossy by nature for the absent-versus-explicit-null distinction, which is exactly why callers
    /// that HAVE the raw document must pass it rather than round-tripping through this. It exists for
    /// the call sites that only ever held a bound object, where no presence information was available
    /// to lose in the first place.
    /// </remarks>
    public static JsonObject? ToDocument<T>(T? value) where T : class
        => value is null ? null : JsonSerializer.SerializeToNode(value, DocumentOptions)?.AsObject();

    /// <summary>
    /// Overlays a world-level and agent-level <see cref="FileAccessPolicyConfig"/> through the same
    /// engine, for the call site that holds only bound objects.
    /// </summary>
    /// <remarks>
    /// <see cref="FileAccessPolicyConfig"/> is classified <c>ReplaceAsUnit</c> on
    /// <see cref="AgentDefinitionConfig"/> because it is a security boundary: a half-inherited path
    /// allowlist is worse than either layer's, since it grants access neither layer intended. The
    /// nested resolver here therefore leaves the leaf policies unclassified, which resolves to
    /// ScalarOverride - each field replaced wholesale by the agent when present.
    /// </remarks>
    public static FileAccessPolicyConfig? OverlayFileAccess(
        FileAccessPolicyConfig? worldLevel,
        FileAccessPolicyConfig? agentLevel)
    {
        if (worldLevel is null && agentLevel is null)
            return null;

        var engine = new ConfigInheritanceEngine(EmptyPolicies);

        var result = engine.Overlay(
        [
            new ConfigLayer(DefaultsLayerName, ToDocument(worldLevel)),
            new ConfigLayer(AgentLayerName, ToDocument(agentLevel)),
        ]);

        return result.Document.Deserialize<FileAccessPolicyConfig>(DocumentOptions);
    }

    /// <summary>
    /// Resolver for nested types with no declared classifications of their own, so every leaf takes
    /// the engine's default ScalarOverride.
    /// </summary>
    private static readonly IConfigPolicyResolver EmptyPolicies =
        new MapConfigPolicyResolver(new Dictionary<string, ConfigInheritancePolicy>(StringComparer.Ordinal));

    /// <summary>
    /// Builds the path-to-policy map for <see cref="AgentDefinitionConfig"/> from its declared
    /// <see cref="ConfigInheritanceAttribute"/> classifications.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keys are camelCase LEAF names to match the document, because the engine resolves policies
    /// against document paths. Note the registry's <c>PropertyPath</c> is type-qualified
    /// (<c>AgentDefinitionConfig.Heartbeat</c>), so the type prefix must be stripped before
    /// conversion.
    /// </para>
    /// <para>
    /// Getting this wrong is silent. A malformed key misses on every lookup, the engine falls back to
    /// its default <see cref="ConfigInheritancePolicy.ScalarOverride"/>, and the result is a merge
    /// that still produces plausible output - scalars behave correctly and only nested blocks are
    /// wrong. The first draft converted the qualified path wholesale, producing
    /// <c>agentDefinitionConfig.Heartbeat</c>, and ten of twelve parity cases still passed. That is
    /// why the suite asserts the map contains the keys the engine will actually look up, rather than
    /// merely asserting it is non-empty.
    /// </para>
    /// </remarks>
    private static Dictionary<string, ConfigInheritancePolicy> BuildPolicyMap()
    {
        var map = new Dictionary<string, ConfigInheritancePolicy>(StringComparer.Ordinal);

        foreach (var classification in ConfigInheritanceRegistry.GetClassifications(typeof(AgentDefinitionConfig)))
        {
            var leaf = classification.PropertyPath;
            var cut = leaf.LastIndexOf('.');
            if (cut >= 0)
                leaf = leaf[(cut + 1)..];

            map[JsonNamingPolicy.CamelCase.ConvertName(leaf)] = classification.Policy;
        }

        return map;
    }

    /// <summary>
    /// The path-to-policy map the engine resolves against, keyed exactly as document paths appear.
    /// Exposed so a test can assert the keys are the ones the engine will actually look up.
    /// </summary>
    internal static IReadOnlyDictionary<string, ConfigInheritancePolicy> PolicyMap => BuildPolicyMap();
}

/// <summary>
/// The effective agent configuration plus the provenance of every path that contributed to it.
/// </summary>
/// <param name="Effective">The merged configuration.</param>
/// <param name="Overlay">
/// The raw overlay result, carrying per-path provenance. Retained so a caller can answer "why does
/// this agent have this value" without re-running the merge.
/// </param>
public sealed record AgentInheritanceResult(
    AgentDefinitionConfig Effective,
    ConfigOverlayResult Overlay);
