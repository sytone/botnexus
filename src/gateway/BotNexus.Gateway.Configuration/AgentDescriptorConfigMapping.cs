using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Single source of truth for the persistence-mapping decision of every
/// <see cref="AgentDescriptor"/> property that the portal / REST agent-management path can
/// create or edit.
/// </summary>
/// <remarks>
/// <para>
/// Issue #2055: the portal agent editor is not a separate descriptor store - a portal-created or
/// portal-edited agent is persisted under <c>PlatformConfig.Agents</c> by
/// <see cref="PlatformConfigAgentWriter"/> and re-materialised by
/// <see cref="PlatformConfigAgentSource"/> on the next config reload. For that round trip to be
/// lossless, every persisted/portal-editable descriptor property must have an explicit mapping
/// decision: either it is <see cref="Persisted"/> (the writer serialises it and the source reads
/// it back to the same effective value) or it is <see cref="UnsupportedForPersistence"/> (a
/// deliberate decision, documented here, that the property is not round-tripped through the config
/// source).
/// </para>
/// <para>
/// The field-parity fitness test asserts that the union of these two sets exactly covers every
/// settable public property of <see cref="AgentDescriptor"/>. A new portal-editable or persisted
/// descriptor property therefore cannot be added without an explicit decision recorded here - the
/// fitness test fails until the property is classified, forcing the writer mapping to be
/// considered rather than silently dropped.
/// </para>
/// </remarks>
public static class AgentDescriptorConfigMapping
{
    /// <summary>
    /// Descriptor properties that <see cref="PlatformConfigAgentWriter.SaveAsync"/> persists and
    /// <see cref="PlatformConfigAgentSource"/> reads back so a create/edit survives a real config
    /// reload with the same effective value.
    /// </summary>
    /// <remarks>
    /// <see cref="AgentDescriptor.Kind"/> is persisted-by-default: the writer never emits
    /// <c>kind</c> for the only value it accepts (<see cref="AgentKind.Named"/>), and the source
    /// defaults an omitted <c>kind</c> to <see cref="AgentKind.Named"/>, so a named agent
    /// round-trips unchanged. <see cref="AgentKind.SubAgent"/> is rejected on both the REST and
    /// config paths and is never written.
    /// </remarks>
    public static readonly IReadOnlySet<string> Persisted = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(AgentDescriptor.AgentId),
        nameof(AgentDescriptor.DisplayName),
        nameof(AgentDescriptor.Kind),
        nameof(AgentDescriptor.Emoji),
        nameof(AgentDescriptor.Description),
        nameof(AgentDescriptor.ModelId),
        nameof(AgentDescriptor.ApiProvider),
        nameof(AgentDescriptor.SystemPromptFile),
        nameof(AgentDescriptor.SystemPromptFiles),
        nameof(AgentDescriptor.ToolIds),
        nameof(AgentDescriptor.AllowedModelIds),
        nameof(AgentDescriptor.SubAgentIds),
        nameof(AgentDescriptor.SubAgentRoles),
        nameof(AgentDescriptor.IsolationStrategy),
        nameof(AgentDescriptor.CacheRetentionMode),
        nameof(AgentDescriptor.Thinking),
        nameof(AgentDescriptor.ContextWindow),
        nameof(AgentDescriptor.MaxConcurrentSessions),
        nameof(AgentDescriptor.Metadata),
        nameof(AgentDescriptor.IsolationOptions),
        nameof(AgentDescriptor.Memory),
        nameof(AgentDescriptor.Soul),
        nameof(AgentDescriptor.Heartbeat),
        nameof(AgentDescriptor.DateTimeInjection),
        nameof(AgentDescriptor.SessionAccessLevel),
        nameof(AgentDescriptor.SessionAllowedAgents),
        nameof(AgentDescriptor.ConversationAccessLevel),
        nameof(AgentDescriptor.ConversationAllowedAgents),
        nameof(AgentDescriptor.FileAccess),
        nameof(AgentDescriptor.ExtensionConfig),
        nameof(AgentDescriptor.ShellCommand),
    };

    /// <summary>
    /// Descriptor properties deliberately not round-tripped through the platform-config source.
    /// Each entry is an explicit decision (see rationale below), not an oversight, so the portal
    /// must treat these as read-only / unsupported rather than silently accepting and discarding
    /// them.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><see cref="AgentDescriptor.Order"/> - display-ordering hint; there is no
    /// <c>agents.&lt;id&gt;.order</c> config field and <see cref="PlatformConfigAgentSource"/>
    /// never reads one, so it cannot round-trip through config and is not persisted.</item>
    /// <item><see cref="AgentDescriptor.SystemPrompt"/> - inline prompt text. The persisted
    /// mechanism for agent prompts is the prompt-file list (<see cref="AgentDescriptor.SystemPromptFile"/>
    /// / <see cref="AgentDescriptor.SystemPromptFiles"/>); <see cref="PlatformConfigAgentSource"/>
    /// never populates the inline prompt from config, so it is not round-trippable and is not
    /// persisted.</item>
    /// <item><see cref="AgentDescriptor.ConversationRetention"/> - there is no corresponding
    /// <c>AgentDefinitionConfig</c> field and the source never reads one, so it cannot round-trip
    /// and is not persisted.</item>
    /// </list>
    /// </remarks>
    public static readonly IReadOnlySet<string> UnsupportedForPersistence = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(AgentDescriptor.Order),
        nameof(AgentDescriptor.SystemPrompt),
        nameof(AgentDescriptor.ConversationRetention),
    };
}
