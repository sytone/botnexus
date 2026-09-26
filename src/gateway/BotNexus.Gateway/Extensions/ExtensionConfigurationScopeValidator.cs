using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Extensions;

/// <summary>Enforces manifest-declared ownership at the four concrete extension configuration locations.</summary>
public static class ExtensionConfigurationScopeValidator
{
    /// <summary>Describes one unsupported or unknown extension configuration placement.</summary>
    public sealed record Violation(string Path, string ExtensionId, ExtensionConfigurationScope RequiredScope, string Reason);

    /// <summary>Enumerates unsupported and unknown entries without binding, merging, defaults, or effective configuration.</summary>
    public static IReadOnlyList<Violation> FindViolations(JsonObject document, IReadOnlyList<LoadedExtension> loadedExtensions)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(loadedExtensions);

        var manifests = loadedExtensions.ToDictionary(extension => extension.ExtensionId, StringComparer.OrdinalIgnoreCase);
        List<Violation> violations = [];

        AddBag(document["world"]?["extensions"], "world.extensions", ExtensionConfigurationScope.World);
        AddBag(document["gateway"]?["extensions"], "gateway.extensions", ExtensionConfigurationScope.Gateway);
        if (document["agents"] is JsonObject agents)
        {
            AddBag(agents["defaults"]?["extensions"], "agents.defaults.extensions", ExtensionConfigurationScope.Agent);
            foreach (var (agentId, agentNode) in agents)
            {
                if (!string.Equals(agentId, "defaults", StringComparison.OrdinalIgnoreCase))
                    AddBag(agentNode?["extensions"], $"agents.{agentId}.extensions", ExtensionConfigurationScope.Agent);
            }
        }

        return violations;

        void AddBag(JsonNode? node, string bagPath, ExtensionConfigurationScope requiredScope)
        {
            if (node is not JsonObject bag)
                return;

            foreach (var (extensionId, _) in bag)
            {
                var path = $"{bagPath}.{extensionId}";
                if (!manifests.TryGetValue(extensionId, out var manifest))
                {
                    violations.Add(new Violation(path, extensionId, requiredScope,
                        $"Extension configuration at '{path}' is unknown because extension '{extensionId}' is not loaded."));
                }
                else if (!manifest.ConfigurationScopes.Contains(requiredScope))
                {
                    violations.Add(new Violation(path, extensionId, requiredScope,
                        $"Extension configuration at '{path}' is unsupported because extension '{extensionId}' does not declare the '{ScopeName(requiredScope)}' configuration scope."));
                }
            }
        }
    }

    /// <summary>Rejects newly introduced violations and existing violations whose value changed.</summary>
    public static IReadOnlyList<string> ValidateChanges(JsonObject before, JsonObject candidate, IReadOnlyList<LoadedExtension> loadedExtensions)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(loadedExtensions);

        var previousPaths = FindViolations(before, loadedExtensions)
            .Select(violation => violation.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> errors = [];

        foreach (var violation in FindViolations(candidate, loadedExtensions))
        {
            if (previousPaths.Contains(violation.Path)
                && JsonNode.DeepEquals(GetPathValue(before, violation.Path), GetPathValue(candidate, violation.Path)))
                continue;

            errors.Add(violation.Reason);
        }

        return errors;
    }

    /// <summary>Validates agent DTO changes against the same manifest scope contract.</summary>
    public static IReadOnlyList<string> ValidateAgentChanges(AgentDescriptor? previous, AgentDescriptor candidate, IReadOnlyList<LoadedExtension> loadedExtensions)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(loadedExtensions);

        var agentScopedExtensionIds = loadedExtensions
            .Where(extension => extension.ConfigurationScopes.Contains(ExtensionConfigurationScope.Agent))
            .Select(extension => extension.ExtensionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> errors = [];

        foreach (var (extensionId, candidateConfig) in candidate.ExtensionConfig)
        {
            if (previous?.ExtensionConfig.TryGetValue(extensionId, out var previousConfig) == true
                && JsonElement.DeepEquals(previousConfig, candidateConfig))
                continue;

            if (!agentScopedExtensionIds.Contains(extensionId))
                errors.Add($"Extension '{extensionId}' must be loaded and declare the agent configuration scope before its agent configuration can be added or changed.");
        }

        return errors;
    }

    private static JsonNode? GetPathValue(JsonObject document, string path)
    {
        if (TryGetBagValue(document["world"]?["extensions"], "world.extensions", path, out var value)
            || TryGetBagValue(document["gateway"]?["extensions"], "gateway.extensions", path, out value))
            return value;

        if (document["agents"] is not JsonObject agents)
            return null;

        if (TryGetBagValue(agents["defaults"]?["extensions"], "agents.defaults.extensions", path, out value))
            return value;

        foreach (var (agentId, agentNode) in agents)
        {
            if (!string.Equals(agentId, "defaults", StringComparison.OrdinalIgnoreCase)
                && TryGetBagValue(agentNode?["extensions"], $"agents.{agentId}.extensions", path, out value))
                return value;
        }

        return null;
    }

    private static bool TryGetBagValue(JsonNode? node, string bagPath, string path, out JsonNode? value)
    {
        value = null;
        if (node is not JsonObject bag || !path.StartsWith($"{bagPath}.", StringComparison.OrdinalIgnoreCase))
            return false;

        var extensionId = path[(bagPath.Length + 1)..];
        return bag.TryGetPropertyValue(extensionId, out value);
    }

    private static string ScopeName(ExtensionConfigurationScope scope) => scope.ToString().ToLowerInvariant();
}
