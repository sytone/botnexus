using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Skills;

/// <summary>Resolves Skills configuration while keeping named-agent semantics explicit.</summary>
public static class SkillsConfigResolver
{
    /// <summary>
    /// Uses named-agent settings as authored, inherits omitted filesystem permissions from
    /// agent defaults, and combines security acknowledgements from both scopes.
    /// </summary>
    public static SkillsConfig? Resolve(AgentDescriptor descriptor)
    {
        var named = ExtensionConfigBinder.BindNamedAgent<SkillsConfig>(descriptor, SkillsExtensionJson.ExtensionId);
        var defaults = ExtensionConfigBinder.BindAgentDefaults<SkillsConfig>(descriptor, SkillsExtensionJson.ExtensionId);
        if (named is null && defaults is null)
            return null;

        var source = named ?? defaults ?? new SkillsConfig();
        return new SkillsConfig
        {
            Enabled = source.Enabled,
            AllowSymlinkedSkills = ResolvePermission(descriptor, "allowSymlinkedSkills", defaults?.AllowSymlinkedSkills ?? false),
            AutoLoad = source.AutoLoad,
            Disabled = source.Disabled,
            Allowed = source.Allowed,
            MaxLoadedSkills = source.MaxLoadedSkills,
            MaxSkillContentChars = source.MaxSkillContentChars,
            TrustMode = source.TrustMode,
            AllowSkillCreation = source.AllowSkillCreation,
            AllowSkillDeletion = source.AllowSkillDeletion,
            AllowSharedSkillManagement = ResolvePermission(descriptor, "allowSharedSkillManagement",
                defaults?.AllowSharedSkillManagement ?? new SkillsConfig().AllowSharedSkillManagement),
            SecurityAcknowledgements = Combine(defaults?.SecurityAcknowledgements, source.SecurityAcknowledgements)
        };
    }

    private static bool ResolvePermission(AgentDescriptor descriptor, string propertyName, bool defaultValue)
    {
        if (TryReadPermission(descriptor.ExtensionConfig, propertyName, out var namedValue))
            return namedValue;
        if (TryReadPermission(descriptor.DefaultExtensionConfig, propertyName, out var inheritedValue))
            return inheritedValue;
        return defaultValue;
    }

    private static bool TryReadPermission(
        IReadOnlyDictionary<string, JsonElement> scope,
        string propertyName,
        out bool value)
    {
        value = default;
        if (!scope.TryGetValue(SkillsExtensionJson.ExtensionId, out var extension) ||
            extension.ValueKind is not JsonValueKind.Object)
            return false;

        // Match case-insensitive typed binding: the last authored boolean wins.
        var found = false;
        foreach (var property in extension.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = property.Value.GetBoolean();
                found = true;
            }
        }

        return found;
    }

    private static List<Security.SkillSecurityAcknowledgement>? Combine(
        IReadOnlyList<Security.SkillSecurityAcknowledgement>? defaults,
        IReadOnlyList<Security.SkillSecurityAcknowledgement>? named)
    {
        if (defaults is not { Count: > 0 } && named is not { Count: > 0 })
            return null;
        return [.. defaults ?? [], .. named ?? []];
    }
}
