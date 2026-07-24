namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Client-side mirror of the server's write-once conversation origination trigger (epic #2300,
/// slice A shipped the domain enum). This is the answer to "why does this conversation exist?"
/// and it is supplied by the server on every conversation payload.
/// </summary>
/// <remarks>
/// <para>
/// The client deliberately re-declares the enum rather than referencing the domain assembly: the
/// Blazor client is a separate deployment unit and must not take a dependency on gateway domain
/// types. The wire contract is the JSON string emitted by the server, parsed by
/// <see cref="ConversationOrigin.ParseSource"/>.
/// </para>
/// <para>
/// <b>Immutable on the client.</b> <see cref="ConversationState.Source"/> is <c>init</c>-only and is
/// seeded straight from the server payload. No inbound SignalR event may write it. That is the
/// exact mutable-flag defect class fixed for agents in #2248 and it is structurally prevented here.
/// </para>
/// </remarks>
public enum ConversationSource
{
    /// <summary>
    /// User/channel-driven: a human sent the first inbound message on a channel binding, or
    /// explicitly created the conversation. First so it is the back-compat default for any payload
    /// from a server that predates the field.
    /// </summary>
    Channel = 0,

    /// <summary>Schedule-driven: a cron job or heartbeat tick minted the conversation for its run.</summary>
    Cron = 1,

    /// <summary>Inbound-webhook driven: an external system POSTed to a webhook registration.</summary>
    Webhook = 2,

    /// <summary>
    /// Agent-initiated: an agent minted the conversation itself, via the <c>conversation_new</c>
    /// tool, an agent-to-agent converse handshake, or sub-agent supervision. Use
    /// <see cref="ConversationKind"/> to tell those three apart.
    /// </summary>
    Agent = 3
}

/// <summary>
/// Client-side mirror of the server's conversation citizen-pairing discriminator. Orthogonal to
/// <see cref="ConversationSource"/>: <c>Kind</c> is "who is talking to whom", <c>Source</c> is
/// "why does this exist". Together they fully determine every render decision the portal makes.
/// </summary>
public enum ConversationKind
{
    /// <summary>A human talking to one or more named agents. The historical default.</summary>
    HumanAgent = 0,

    /// <summary>Two named agents in a peer exchange. No human is in the loop.</summary>
    AgentAgent = 1,

    /// <summary>A named agent supervising a spawned sub-agent. No human is in the loop.</summary>
    AgentSubAgent = 2
}

/// <summary>
/// Parses the server-supplied conversation origin strings into the client's typed enums. Parsing is
/// tolerant and total: an unknown, empty or absent value falls back to the back-compat default so a
/// newer server introducing a value this client does not know never breaks rendering.
/// </summary>
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
}
