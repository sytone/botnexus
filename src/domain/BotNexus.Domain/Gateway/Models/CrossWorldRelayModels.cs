namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Gateway-to-gateway relay request for cross-world agent communication.
/// </summary>
public sealed record CrossWorldRelayRequest
{
    public required string SourceWorldId { get; init; }
    public required string SourceAgentId { get; init; }
    public required string TargetAgentId { get; init; }
    public required string Message { get; init; }
    public required string ConversationId { get; init; }
    public string? SourceSessionId { get; init; }
    public string? RemoteSessionId { get; init; }
}

/// <summary>
/// Response returned by a remote gateway relay call.
/// </summary>
public sealed record CrossWorldRelayResponse
{
    public required string Response { get; init; }
    public required string Status { get; init; }
    public required string SessionId { get; init; }
}
