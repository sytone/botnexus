using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Skills;

/// <summary>Resolves Skills configuration while keeping named-agent semantics explicit.</summary>
public static class SkillsConfigResolver
{
    /// <summary>
    /// Uses every named-agent property as authored and combines only security acknowledgements
    /// from agent defaults, because the acknowledgement endpoint persists in that scope.
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
            AutoLoad = source.AutoLoad,
            Disabled = source.Disabled,
            Allowed = source.Allowed,
            MaxLoadedSkills = source.MaxLoadedSkills,
            MaxSkillContentChars = source.MaxSkillContentChars,
            TrustMode = source.TrustMode,
            AllowSkillCreation = source.AllowSkillCreation,
            AllowSkillDeletion = source.AllowSkillDeletion,
            AllowSharedSkillManagement = ResolveSharedManagement(descriptor, defaults),
            SecurityAcknowledgements = Combine(defaults?.SecurityAcknowledgements, source.SecurityAcknowledgements)
        };
    }

    private static bool ResolveSharedManagement(AgentDescriptor descriptor, SkillsConfig? defaults)
    {
        if (TryReadSharedManagement(descriptor.ExtensionConfig, out var namedValue))
            return namedValue;
        if (TryReadSharedManagement(descriptor.DefaultExtensionConfig, out var defaultValue))
            return defaultValue;
        return defaults?.AllowSharedSkillManagement ?? new SkillsConfig().AllowSharedSkillManagement;
    }

    private static bool TryReadSharedManagement(
        IReadOnlyDictionary<string, JsonElement> scope,
        out bool value)
    {
        value = default;
        if (!scope.TryGetValue(SkillsExtensionJson.ExtensionId, out var extension) ||
            extension.ValueKind is not JsonValueKind.Object)
            return false;

        foreach (var property in extension.EnumerateObject())
        {
            if (string.Equals(property.Name, "allowSharedSkillManagement", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = property.Value.GetBoolean();
                return true;
            }
        }

        return false;
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
