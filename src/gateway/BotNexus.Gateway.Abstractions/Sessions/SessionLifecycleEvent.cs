using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

public enum SessionLifecycleEventType { Created, MessageAdded, Closed, Expired, Deleted }

public sealed record SessionLifecycleEvent(
    string SessionId,
    string AgentId,
    SessionLifecycleEventType Type,
    GatewaySession? Session);
