using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #1624: <see cref="GatewayEventHandler"/>'s reconnect-recovery failure must route through
/// <see cref="ILogger"/> (structured) rather than <c>Console.Error</c>, while still clearing stale
/// streaming state unconditionally.
/// </summary>
public sealed class GatewayEventHandlerLoggingTests
{
    private readonly ClientStateStore _store = new();
    private readonly RecordingLogger<GatewayEventHandler> _logger = new();
    private readonly GatewayEventHandler _handler;

    public GatewayEventHandlerLoggingTests()
    {
        // A fresh, unconnected GatewayHubConnection makes SubscribeAllAsync fail inside
        // HandleReconnectedAsync -- the exact recovery-failure path that previously wrote to
        // Console.Error.
        _handler = new GatewayEventHandler(_store, new GatewayHubConnection(), _logger);

        _store.UpsertAgent(new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Agent 1",
            IsConnected = true,
            ActiveConversationId = "conv-1"
        });
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Conversation 1",
            HistoryLoaded = true
        };
    }

    [Fact]
    public async Task HandleReconnected_recovery_failure_logs_error_and_still_clears_stream_state()
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        agent.IsStreaming = true;
        conv.StreamState.IsStreaming = true;
        conv.StreamState.Buffer = "partial";

        // Disconnect while streaming, then reconnect: SubscribeAllAsync fails on the unconnected hub.
        _handler.HandleReconnecting();
        await _handler.HandleReconnectedAsync();

        // The recovery failure is logged at Error (was Console.Error).
        var error = _logger.Entries.FirstOrDefault(e => e.Level == LogLevel.Error);
        error.ShouldNotBeNull("Reconnect recovery failure must be logged at Error.");
        error!.Message.ShouldContain("Reconnect recovery");

        // Stale streaming state is still cleared unconditionally (#759 invariant preserved).
        conv.StreamState.IsStreaming.ShouldBeFalse();
        conv.StreamState.Buffer.ShouldBe(string.Empty);
        conv.HistoryLoaded.ShouldBeFalse();
    }

    [Fact]
    public void Handler_has_no_console_error_writes_remaining()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BotNexus.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("Could not locate BotNexus.slnx from test base directory.");

        var path = Path.Combine(
            dir.FullName,
            "src", "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient.Core",
            "Services",
            "GatewayEventHandler.cs");
        File.ReadAllText(path).ShouldNotContain(
            "Console.Error",
            customMessage: "GatewayEventHandler must not write to Console.Error -- use ILogger.");
    }
}
