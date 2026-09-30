using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Specifies supported values for session lifecycle event type.
/// </summary>
public enum SessionLifecycleEventType { Created, MessageAdded, Closed, TerminalFailure, Expired, Deleted }

/// <summary>
/// Represents session lifecycle event.
/// </summary>
public sealed record SessionLifecycleEvent(
    string SessionId,
    string AgentId,
    SessionLifecycleEventType Type,
    GatewaySession? Session);
