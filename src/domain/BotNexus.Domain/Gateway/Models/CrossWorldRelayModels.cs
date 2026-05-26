namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Gateway-to-gateway relay request for cross-world agent communication.
/// </summary>
public sealed record CrossWorldRelayRequest
{
    /// <summary>
    /// Gets or sets the source world id.
    /// </summary>
    public required string SourceWorldId { get; init; }
    /// <summary>
    /// Gets or sets the source agent id.
    /// </summary>
    public required string SourceAgentId { get; init; }
    /// <summary>
    /// Gets or sets the target agent id.
    /// </summary>
    public required string TargetAgentId { get; init; }
    /// <summary>
    /// Gets or sets the message.
    /// </summary>
    public required string Message { get; init; }
    /// <summary>
    /// Gets or sets the conversation id that the message belongs to on the source world.
    /// Used by the remote gateway to correlate the response back to the originating conversation.
    /// </summary>
    public required string ConversationId { get; init; }
    /// <summary>
    /// Gets or sets the source session id.
    /// </summary>
    public string? SourceSessionId { get; init; }
    /// <summary>
    /// Gets or sets the remote session id.
    /// </summary>
    public string? RemoteSessionId { get; init; }
}

/// <summary>
/// Response returned by a remote gateway relay call.
/// </summary>
public sealed record CrossWorldRelayResponse
{
    /// <summary>
    /// Gets or sets the response.
    /// </summary>
    public required string Response { get; init; }
    /// <summary>
    /// Gets or sets the status.
    /// </summary>
    public required string Status { get; init; }
    /// <summary>
    /// Gets or sets the session id.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Whether the target agent invoked the <c>finish_agent_exchange</c> tool to signal
    /// completion of the exchange (Phase 8 / F-11). When <c>true</c>, the sender should
    /// stop iterating and return control to the initiating agent. Defaults to <c>false</c>
    /// so older receiver builds without this flag continue to drive turns until
    /// <c>MaxTurns</c> is reached.
    /// </summary>
    public bool ExchangeFinished { get; init; }

    /// <summary>
    /// Optional <c>reason</c> argument the target agent supplied to <c>finish_agent_exchange</c>.
    /// Present only when <see cref="ExchangeFinished"/> is <c>true</c>.
    /// </summary>
    public string? FinishReason { get; init; }

    /// <summary>
    /// Optional <c>summary</c> argument the target agent supplied to <c>finish_agent_exchange</c>.
    /// Present only when <see cref="ExchangeFinished"/> is <c>true</c>.
    /// </summary>
    public string? FinishSummary { get; init; }
}
