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
        if (named is null && defaults?.SecurityAcknowledgements is not { Count: > 0 })
            return null;

        var source = named ?? new SkillsConfig();
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
            AllowSharedSkillManagement = source.AllowSharedSkillManagement,
            SecurityAcknowledgements = Combine(defaults?.SecurityAcknowledgements, source.SecurityAcknowledgements)
        };
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
