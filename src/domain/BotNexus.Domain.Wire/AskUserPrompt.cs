namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Channel-agnostic, render-ready projection of a pending <c>ask_user</c> checkpoint (#2322).
/// </summary>
/// <remarks>
/// <para>
/// This is the single normalized shape every channel renders from, regardless of whether the
/// prompt arrived as a live <c>UserInputRequired</c> stream event, as flattened event metadata,
/// or was rehydrated from the durable <c>PendingAskUserJson</c> payload persisted on the
/// conversation row. It deliberately lives outside any client project so a channel extension
/// (Telegram, Discord, Slack, TUI) can consume it without referencing the Blazor client - the
/// duplication hazard called out in #2322.
/// </para>
/// <para>
/// WASM PAYLOAD NOTE (#2329, #2334): this type lives in the dependency-free
/// <c>BotNexus.Domain.Wire</c> assembly rather than in <c>BotNexus.Domain</c>. The Blazor
/// WebAssembly client renders these prompts, and every assembly reachable from a WASM entry
/// point is downloaded by the browser. <c>BotNexus.Domain</c> references <c>Vogen</c> as a bare
/// <c>PackageReference</c> that flows as a RUNTIME asset (see the note in BotNexus.Domain.csproj -
/// <c>PrivateAssets="all"</c> was tried and empirically rejected because
/// <c>Vogen.ValueObjectValidationException</c> is caught at runtime on the server). Keeping the
/// ask_user wire shapes here is what lets the client share this exact declaration without
/// dragging Vogen.SharedTypes.dll into the browser payload.
/// </para>
/// <para>
/// <see cref="InputType"/> is kept as the raw string rather than <c>AskUserInputType</c> so an
/// unrecognised future input type degrades to a text prompt instead of throwing during
/// deserialization at the channel edge.
/// </para>
/// <para>
/// <see cref="ConversationId"/> is a plain <see cref="string"/> rather than the typed
/// <c>BotNexus.Domain.Primitives.ConversationId</c> value object, because that value object is
/// Vogen-generated and cannot cross into this dependency-free assembly. This is the wire shape;
/// the gateway maps it to and from the typed id at its own boundary (see
/// <c>AskUserPromptProjection</c>), which is exactly where validation belongs. It is nullable
/// because reconciliation can legitimately run before a conversation id is known - flattened
/// event metadata may omit it and the structured fallback may be a partial payload.
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
    public string? ConversationId { get; init; }

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
