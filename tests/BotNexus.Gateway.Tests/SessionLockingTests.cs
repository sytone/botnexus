namespace BotNexus.Gateway.Tests;

// TODO: This suite will compile against concrete lock-management APIs once implemented in GatewayWebSocketHandler.
public sealed class SessionLockingTests
{
    [Fact(Skip = "Awaiting per-session WebSocket lock implementation.")]
    public Task WebSocketHandler_RejectsSecondConnection_ToSameSession()
        => Task.CompletedTask;

    [Fact(Skip = "Awaiting per-session WebSocket lock implementation.")]
    public Task WebSocketHandler_AllowsConnection_ToDifferentSession()
        => Task.CompletedTask;

    [Fact(Skip = "Awaiting per-session WebSocket lock implementation.")]
    public Task WebSocketHandler_ReleasesLock_OnDisconnect()
        => Task.CompletedTask;

    [Fact(Skip = "Awaiting per-session WebSocket lock implementation.")]
    public Task WebSocketHandler_AllowsReconnect_AfterDisconnect()
        => Task.CompletedTask;
}
