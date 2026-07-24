namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// How a conversation is grouped and badged in the portal's conversation list. Derived purely from
/// the immutable <c>(Kind, Source)</c> pair supplied by the server — never from a mutable flag or a
/// session-id string prefix.
/// </summary>
public enum ConversationListGroup
{
    /// <summary>A normal human-driven conversation. Appears in the main list.</summary>
    Normal,

    /// <summary>A schedule-driven run. Grouped under the collapsible "Scheduled" section.</summary>
    Scheduled,

    /// <summary>An inbound-webhook run. Grouped alongside scheduled unattended runs.</summary>
    Automated,

    /// <summary>An agent-initiated thread (peer converse or sub-agent supervision). Read-only observer row.</summary>
    AgentInitiated
}

/// <summary>
/// Deterministic render projection over the immutable conversation origin signal
/// <c>(Kind, Source)</c> plus the current <see cref="SelectionSource"/>. This is the single place
/// the portal decides whether a conversation is read-only, whether the composer is shown, and how
/// the conversation is grouped/badged.
/// </summary>
/// <remarks>
/// <para>
/// Every input is immutable: <see cref="ConversationState.Source"/> and
/// <see cref="ConversationState.Kind"/> are <c>init</c>-only and seeded from the server payload, and
/// the selection source is a read-only projection of the store's single view-selection value
/// (#2246). No inbound SignalR event can move any of them, so no inbound event can flip a user's
/// own conversation to read-only or hide their composer. That is the #2248-class guard, applied to
/// conversations (epic #2300, slice D).
/// </para>
/// <para>
/// The truth table is total over all <c>(Kind, Source)</c> combinations; there is no fallthrough to
/// inference.
/// </para>
/// </remarks>
/// <param name="Kind">The immutable citizen-pairing of the conversation.</param>
/// <param name="Source">The immutable origination trigger of the conversation.</param>
/// <param name="SelectionSource">The source that requested the current active view.</param>
public readonly record struct ConversationRenderProjection(
    ConversationKind Kind,
    ConversationSource Source,
    SelectionSource SelectionSource)
{
    /// <summary>
    /// Builds the projection for a conversation under the supplied selection source.
    /// </summary>
    /// <param name="conversation">The conversation whose immutable origin drives the projection.</param>
    /// <param name="selectionSource">The source that requested the current active view.</param>
    /// <returns>The deterministic render projection.</returns>
    public static ConversationRenderProjection For(
        ConversationState conversation,
        SelectionSource selectionSource)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return new ConversationRenderProjection(conversation.Kind, conversation.Source, selectionSource);
    }

    /// <summary>
    /// True when no human citizen participates in the conversation, so nothing the user types could
    /// ever be delivered into it. Derived from the immutable pair only — the selection source is
    /// deliberately excluded so this stays a property of the conversation itself.
    /// </summary>
    public bool IsUnattended =>
        Kind is ConversationKind.AgentAgent or ConversationKind.AgentSubAgent
        || Source is ConversationSource.Cron or ConversationSource.Webhook or ConversationSource.Agent;

    /// <summary>
    /// True when the conversation must render read-only. That is the case for any unattended
    /// conversation, and additionally whenever the active view was promoted by the explicit
    /// "view sub-agent" observer interaction (<see cref="Services.SelectionSource.SubAgentView"/>).
    /// </summary>
    public bool IsReadOnly =>
        IsUnattended || SelectionSource == Services.SelectionSource.SubAgentView;

    /// <summary>
    /// True when the message composer should be rendered. Exactly the negation of
    /// <see cref="IsReadOnly"/>, named separately so call sites read as intent rather than as a
    /// double negative.
    /// </summary>
    public bool ShowComposer => !IsReadOnly;

    /// <summary>
    /// Which list group/section the conversation belongs to. Agent-initiated pairings win over the
    /// trigger because an agent-supervision thread stays an observer row regardless of what
    /// originally triggered the parent run.
    /// </summary>
    public ConversationListGroup Group =>
        Kind is ConversationKind.AgentAgent or ConversationKind.AgentSubAgent
            ? ConversationListGroup.AgentInitiated
            : Source switch
            {
                ConversationSource.Cron => ConversationListGroup.Scheduled,
                ConversationSource.Webhook => ConversationListGroup.Automated,
                ConversationSource.Agent => ConversationListGroup.AgentInitiated,
                _ => ConversationListGroup.Normal
            };

    /// <summary>
    /// The badge text shown next to the conversation title, or <c>null</c> for a normal
    /// human-driven conversation which carries no badge.
    /// </summary>
    public string? Badge => Group switch
    {
        ConversationListGroup.Scheduled => "Cron",
        ConversationListGroup.Automated => "Webhook",
        ConversationListGroup.AgentInitiated => "Read-only",
        _ => null
    };
}
