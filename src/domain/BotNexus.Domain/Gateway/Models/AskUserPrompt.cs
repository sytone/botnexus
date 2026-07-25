using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Channel-agnostic, render-ready projection of a pending <c>ask_user</c> checkpoint (#2322).
/// </summary>
/// <remarks>
/// <para>
/// This is the single normalized shape every channel renders from, regardless of whether the
/// prompt arrived as a live <c>UserInputRequired</c> stream event, as flattened event metadata,
/// or was rehydrated from the durable <c>PendingAskUserJson</c> payload persisted on the
/// conversation row. It deliberately lives in the domain assembly rather than in a client
/// project so a channel extension (Telegram, Discord, Slack, TUI) can consume it without
/// referencing the Blazor client - the duplication hazard called out in #2322.
/// </para>
/// <para>
/// <see cref="InputType"/> is kept as the raw string rather than
/// <see cref="AskUserInputType"/> so an unrecognised future input type degrades to a text
/// prompt instead of throwing during deserialization at the channel edge.
/// </para>
/// <para>
/// <see cref="ConversationId"/> is the typed <see cref="BotNexus.Domain.Primitives.ConversationId"/>
/// value object rather than a raw string, matching <c>Conversation.ConversationId</c> and
/// <c>AskUserRequest.ConversationId</c>. It is nullable rather than <c>required</c> because
/// reconciliation can legitimately run before a conversation id is known - flattened event metadata
/// may omit it and the structured fallback may be a partial payload. Nullable is the established
/// codebase idiom for an optional conversation id (see <c>InboundMessageContext.RequestedConversationId</c>
/// and <c>CronJob.ConversationId</c>); the Vogen <c>default</c> sentinel is prohibited (VOG009).
/// </para>
/// </remarks>
public sealed record AskUserPrompt
{
    /// <summary>Correlation identifier used when resolving this prompt.</summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// Conversation that owns the prompt and can satisfy it from any bound channel. Null when
    /// neither reconciliation source supplied one.
    /// </summary>
    public ConversationId? ConversationId { get; init; }

    /// <summary>Prompt text presented to the user.</summary>
    public required string Prompt { get; init; }

    /// <summary>Raw input-type token (<c>FreeForm</c>, <c>SingleChoice</c>, ...).</summary>
    public required string InputType { get; init; }

    /// <summary>Optional structured choices for choice-based prompts.</summary>
    public IReadOnlyList<AskUserPromptChoice>? Choices { get; init; }

    /// <summary>Whether more than one choice may be selected.</summary>
    public bool AllowMultiple { get; init; }

    /// <summary>Whether a custom free-form answer is accepted.</summary>
    public bool AllowFreeForm { get; init; }

    /// <summary>Absolute expiry instant when the prompt carries a timeout.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>True when the prompt offers structured choices a channel could render as buttons.</summary>
    public bool HasChoices => Choices is { Count: > 0 };
}

/// <summary>
/// A single selectable option on an <see cref="AskUserPrompt"/>.
/// </summary>
/// <param name="Value">Machine-stable value returned when selected.</param>
/// <param name="Label">Display label shown to the user; never empty.</param>
/// <param name="Description">Optional helper text describing the option.</param>
public sealed record AskUserPromptChoice(string Value, string Label, string? Description = null);
