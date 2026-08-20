namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// The single owner of the chat message role vocabulary for the Blazor client (#3456).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Four near-identical lower-case-and-map switch expressions lived in
/// <c>AgentInteractionService</c>, <c>PortalLoadService</c>, <c>GatewayEventHandler</c> and
/// <c>ClientStateStore</c>, and one of them carried a comment stating outright that it mirrored
/// another. Four copies of one rule drift silently: two of them already handled a blank role and
/// two did not, and two lower-cased an unrecognised role while two preserved it. A comment
/// acknowledging a mirror is the clearest possible signal that the seam is in the wrong place --
/// the same argument <see cref="ConversationOrigin"/> makes under #2305.
/// </para>
/// <para>
/// <b>Normalisation is total.</b> <see cref="Normalize"/> never throws and never returns null. It
/// is case-insensitive, trims surrounding whitespace, and maps <c>null</c>, <c>""</c> and
/// whitespace-only input to <see cref="Assistant"/>. That blank default is the pre-existing
/// post-as-assistant behaviour of the streaming flush paths: the pending role is absent for every
/// ordinary streamed reply, and such a reply is an agent message.
/// </para>
/// <para>
/// <b>Unrecognised roles pass through verbatim, they do NOT become <see cref="Assistant"/>.</b>
/// This is a deliberate departure from the letter of the issue's acceptance criterion 2. A
/// deployed client can be older than the gateway it talks to, exactly as
/// <see cref="ConversationOrigin"/> documents. Collapsing an unknown role to <c>Assistant</c>
/// would render a future platform-generated message -- a notification, a moderation notice -- as
/// an agent bubble, which is <em>misattribution</em>: it tells the user the agent said something
/// it did not. Preserving the token instead degrades to an unstyled bubble, which is cosmetic.
/// A blank role carries no such information and so keeps the assistant default.
/// </para>
/// <para>
/// <b>The predicates are token tests, not <see cref="Normalize"/> composed with equality.</b>
/// <see cref="IsAssistant"/> returns <c>false</c> for a blank role even though
/// <see cref="Normalize"/> maps blank to <see cref="Assistant"/>. The two answer different
/// questions: <see cref="Normalize"/> picks the role to <em>store</em> on a message that is being
/// created, whereas the predicates ask what an <em>already stored</em> message is. A stored
/// message with a blank role is not an assistant message, and treating it as one would change how
/// the surfaces render it.
/// </para>
/// </remarks>
public static class MessageRole
{
    /// <summary>Canonical display casing for an agent message.</summary>
    public const string Assistant = "Assistant";

    /// <summary>Canonical display casing for a human message.</summary>
    public const string User = "User";

    /// <summary>Canonical display casing for a platform-generated informational message.</summary>
    public const string System = "System";

    /// <summary>Canonical display casing for a tool-call message.</summary>
    public const string Tool = "Tool";

    /// <summary>Canonical display casing for a failure message.</summary>
    public const string Error = "Error";

    /// <summary>CSS bubble-side token for a human message.</summary>
    private const string UserCss = "user";

    /// <summary>CSS bubble-side token for every non-human message.</summary>
    private const string AssistantCss = "assistant";

    /// <summary>
    /// Maps a raw wire role onto the canonical display casing the surfaces key their rendering off.
    /// </summary>
    /// <param name="role">
    /// The raw role, e.g. <c>"assistant"</c>, <c>"USER"</c>, <c>null</c> or <c>""</c>.
    /// Case-insensitive and surrounding whitespace is ignored.
    /// </param>
    /// <returns>
    /// One of <see cref="Assistant"/>, <see cref="User"/>, <see cref="System"/>,
    /// <see cref="Tool"/>, <see cref="Error"/>; <see cref="Assistant"/> when <paramref name="role"/>
    /// is null, empty or whitespace; otherwise the trimmed input verbatim.
    /// </returns>
    public static string Normalize(string? role)
    {
        var trimmed = role?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return Assistant;

        return trimmed.ToLowerInvariant() switch
        {
            "user" => User,
            "assistant" => Assistant,
            "tool" => Tool,
            "error" => Error,
            "system" => System,
            // Forward-compat: an unknown role from a newer gateway keeps its own identity rather
            // than being misattributed to the agent. See the type-level remarks.
            _ => trimmed,
        };
    }

    /// <summary>
    /// Asks whether an already-stored message is an agent message.
    /// </summary>
    /// <param name="role">The stored role. Case-insensitive; blank yields <c>false</c>.</param>
    /// <returns><c>true</c> only when the role is literally the assistant token.</returns>
    public static bool IsAssistant(string? role) =>
        string.Equals(role?.Trim(), Assistant, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Asks whether an already-stored message is a human message.
    /// </summary>
    /// <param name="role">The stored role. Case-insensitive; blank yields <c>false</c>.</param>
    /// <returns><c>true</c> only when the role is literally the user token.</returns>
    public static bool IsUser(string? role) =>
        string.Equals(role?.Trim(), User, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the lower-case CSS bubble-side token for a stored role.
    /// </summary>
    /// <remarks>
    /// This is intentionally binary, not a lower-cased role name: the chat bubble has two sides,
    /// so every non-human role -- system, tool, error and anything unrecognised -- shares the
    /// agent side. Callers that need to distinguish those roles must test the role itself.
    /// </remarks>
    /// <param name="role">The stored role. Case-insensitive.</param>
    /// <returns><c>"user"</c> for a human message, otherwise <c>"assistant"</c>.</returns>
    public static string CssRole(string? role) => IsUser(role) ? UserCss : AssistantCss;
}
