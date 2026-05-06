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
}
