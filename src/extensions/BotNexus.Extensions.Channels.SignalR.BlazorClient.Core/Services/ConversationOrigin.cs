namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Parses the server-supplied conversation origin strings into the <b>canonical</b>
/// <see cref="ConversationSource"/> / <see cref="ConversationKind"/> enums declared once in
/// <c>BotNexus.Gateway.Abstractions.Models</c> (epic #2300).
/// </summary>
/// <remarks>
/// <para>
/// <b>Single declaration, not a mirror (#2305).</b> An earlier slice re-declared both enums
/// client-side on the reasoning that the Blazor client is a separate deployment unit. That is a
/// duplicated contract that can silently drift: adding a value server-side fails no client build,
/// it just degrades to the tolerant-parse fallback and renders wrong. The client now references the
/// canonical declaration directly. <c>BotNexus.Domain</c> is a pure model assembly, so this is a
/// model dependency, not a gateway-host dependency.
/// </para>
/// <para>
/// <b>Tolerant parsing is still correct.</b> Sharing the enum removes <em>drift</em>, but a
/// deployed client can still be older than the server it talks to. Parsing therefore remains total:
/// an unknown, empty or absent wire value falls back to the back-compat default rather than
/// throwing, so a newer server introducing a value this client build does not know never breaks
/// rendering. Forward-compat and a shared contract are complementary, not alternatives.
/// </para>
/// <para>
/// <b>Immutable on the client.</b> <see cref="ConversationState.Source"/> and
/// <see cref="ConversationState.Kind"/> are <c>init</c>-only and seeded straight from the server
/// payload. No inbound SignalR event may write either. That is the exact mutable-flag defect class
/// fixed for agents in #2248, structurally prevented here.
/// </para>
/// </remarks>
public static class ConversationOrigin
{
    /// <summary>
    /// Parses the <c>source</c> field of a conversation payload. Unknown/empty values return
    /// <see cref="ConversationSource.Channel"/>, matching the server's default-value contract.
    /// </summary>
    /// <param name="value">The raw wire value, e.g. <c>"Cron"</c>. Case-insensitive.</param>
    /// <returns>The parsed source, or <see cref="ConversationSource.Channel"/> when unrecognised.</returns>
    public static ConversationSource ParseSource(string? value) =>
        Enum.TryParse<ConversationSource>(value, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : ConversationSource.Channel;

    /// <summary>
    /// Parses the <c>kind</c> field of a conversation payload. Unknown/empty values return
    /// <see cref="ConversationKind.HumanAgent"/>, matching the server's default-value contract.
    /// </summary>
    /// <param name="value">The raw wire value, e.g. <c>"AgentSubAgent"</c>. Case-insensitive.</param>
    /// <returns>The parsed kind, or <see cref="ConversationKind.HumanAgent"/> when unrecognised.</returns>
    public static ConversationKind ParseKind(string? value) =>
        Enum.TryParse<ConversationKind>(value, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : ConversationKind.HumanAgent;

    /// <summary>
    /// Parses the <c>visibility</c> field of a conversation payload (#2340). Unknown/empty values
    /// return <see cref="ConversationVisibility.UserFacing"/>, matching the server's default-value
    /// contract.
    /// </summary>
    /// <remarks>
    /// The fallback deliberately fails <em>open</em> (visible) rather than closed. A client older
    /// than its server would otherwise interpret a newly-added visibility value as "unknown" and, if
    /// that meant hidden, silently empty the user's conversation list on a server upgrade. An
    /// unexpectedly visible row is a cosmetic defect; an unexpectedly missing one looks like data
    /// loss.
    /// </remarks>
    /// <param name="value">The raw wire value, e.g. <c>"InternalHidden"</c>. Case-insensitive.</param>
    /// <returns>The parsed visibility, or <see cref="ConversationVisibility.UserFacing"/> when unrecognised.</returns>
    public static ConversationVisibility ParseVisibility(string? value) =>
        Enum.TryParse<ConversationVisibility>(value, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : ConversationVisibility.UserFacing;
}
