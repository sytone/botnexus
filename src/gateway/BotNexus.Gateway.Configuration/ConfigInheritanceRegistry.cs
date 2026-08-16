using System.Collections.Concurrent;
using System.Reflection;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// One property's declared inheritance behaviour, resolved from
/// <see cref="ConfigInheritanceAttribute"/>.
/// </summary>
/// <param name="PropertyPath">
/// Dotted path from the root of the configuration graph, for example
/// <c>AgentDefinitionConfig.Heartbeat</c>. Used verbatim in fitness-test failure messages so a
/// failure names the property an author must edit rather than describing it.
/// </param>
/// <param name="Policy">The declared behaviour.</param>
/// <param name="Strategy">Named strategy when <paramref name="Policy"/> is Custom, otherwise null.</param>
/// <param name="Justification">Recorded reasoning, where the policy requires one.</param>
public sealed record ConfigInheritanceClassification(
    string PropertyPath,
    ConfigInheritancePolicy Policy,
    string? Strategy,
    string? Justification);

/// <summary>
/// Queryable view over the inheritance classifications declared on a configuration type.
/// </summary>
/// <remarks>
/// <para>
/// Exists so consumers - the merge engine, provenance reporting, and the configuration UI - can ask
/// what a property is SUPPOSED to do instead of each re-deriving it from the shape of a merge helper.
/// That re-derivation is the root of the drift #2137 documents: five places independently decide how
/// one property inherits, and nothing detects when they disagree.
/// </para>
/// <para>
/// Results are cached per type. The classification is compile-time metadata and cannot change during
/// a process lifetime, and the reflection cost would otherwise be paid on every effective-config
/// resolution.
/// </para>
/// </remarks>
public static class ConfigInheritanceRegistry
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<ConfigInheritanceClassification>> Cache = new();

    /// <summary>
    /// Returns the declared classification for every public instance property on
    /// <paramref name="configType"/> that carries one.
    /// </summary>
    /// <remarks>
    /// A property with no attribute is omitted rather than defaulted. Inventing a default here would
    /// defeat the fitness test, which detects an unclassified property precisely by its absence -
    /// a silently-defaulted classification is indistinguishable from a considered one.
    /// </remarks>
    public static IReadOnlyList<ConfigInheritanceClassification> GetClassifications(Type configType)
    {
        ArgumentNullException.ThrowIfNull(configType);

        return Cache.GetOrAdd(configType, static type =>
        {
            var results = new List<ConfigInheritanceClassification>();

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var attribute = property.GetCustomAttribute<ConfigInheritanceAttribute>(inherit: false);
                if (attribute is null)
                    continue;

                results.Add(new ConfigInheritanceClassification(
                    $"{type.Name}.{property.Name}",
                    attribute.Policy,
                    attribute.Strategy,
                    attribute.Justification));
            }

            return results;
        });
    }

    /// <summary>
    /// Returns the classification for a single property, or null when it carries none.
    /// </summary>
    public static ConfigInheritanceClassification? GetClassification(Type configType, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(configType);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        var path = $"{configType.Name}.{propertyName}";
        foreach (var classification in GetClassifications(configType))
        {
            if (string.Equals(classification.PropertyPath, path, StringComparison.Ordinal))
                return classification;
        }

        return null;
    }
}
