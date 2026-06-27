using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Verifies that cache token counts (CacheRead, CacheWrite) flowing through
/// MessageEnd events are attached to the resulting ChatMessage.
/// </summary>
public sealed class CacheTokenFlowTests
{
    private static (GatewayEventHandler handler, ClientStateStore store) BuildHandlerAndStore()
    {
        var store = new ClientStateStore();
        store.UpsertAgent(new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Agent",
            IsConnected = true,
            SessionId = "sess-1",
            ActiveConversationId = "conv-1"
        });
        var agent = store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Test",
            ActiveSessionId = "sess-1"
        };
        store.RegisterSession("agent-1", "sess-1");

        var handler = new GatewayEventHandler(store, new GatewayHubConnection(), Microsoft.Extensions.Logging.Abstractions.NullLogger<GatewayEventHandler>.Instance);
        return (handler, store);
    }

    private static ConversationState GetConv(ClientStateStore store)
        => store.GetAgent("agent-1")!.Conversations["conv-1"];

    [Fact]
    public void HandleMessageEnd_WithCacheTokens_AttachesToChatMessage()
    {
        var (handler, store) = BuildHandlerAndStore();

        var conv = GetConv(store);
        conv.StreamState.Buffer = "response text";
        conv.StreamState.IsStreaming = true;

        handler.HandleMessageEnd(new AgentStreamEvent
        {
            SessionId = "sess-1",
            Usage = new StreamUsagePayload
            {
                InputTokens = 200,
                OutputTokens = 80,
                CacheRead = 150,
                CacheWrite = 50
            }
        });

        var added = GetConv(store).Messages.Last();
        Assert.Equal(200, added.InputTokens);
        Assert.Equal(80, added.OutputTokens);
        Assert.Equal(150, added.CacheRead);
        Assert.Equal(50, added.CacheWrite);
    }

    [Fact]
    public void HandleMessageEnd_WithoutUsage_LeavesTokenFieldsNull()
    {
        var (handler, store) = BuildHandlerAndStore();

        var conv = GetConv(store);
        conv.StreamState.Buffer = "response text";
        conv.StreamState.IsStreaming = true;

        handler.HandleMessageEnd(new AgentStreamEvent { SessionId = "sess-1", Usage = null });

        var added = GetConv(store).Messages.Last();
        Assert.Null(added.InputTokens);
        Assert.Null(added.OutputTokens);
        Assert.Null(added.CacheRead);
        Assert.Null(added.CacheWrite);
    }

    [Fact]
    public void HandleMessageEnd_WithPartialUsage_AttachesAvailableTokens()
    {
        // Some providers only report input/output without cache (e.g. OpenAI)
        var (handler, store) = BuildHandlerAndStore();

        var conv = GetConv(store);
        conv.StreamState.Buffer = "response text";
        conv.StreamState.IsStreaming = true;

        handler.HandleMessageEnd(new AgentStreamEvent
        {
            SessionId = "sess-1",
            Usage = new StreamUsagePayload
            {
                InputTokens = 100,
                OutputTokens = 30,
                CacheRead = null,
                CacheWrite = null
            }
        });

        var added = GetConv(store).Messages.Last();
        Assert.Equal(100, added.InputTokens);
        Assert.Equal(30, added.OutputTokens);
        Assert.Null(added.CacheRead);
        Assert.Null(added.CacheWrite);
    }

    [Fact]
    public void StreamUsagePayload_HasExpectedJsonPropertyNames()
    {
        // Verify the payload round-trips correctly through JSON
        var payload = new StreamUsagePayload
        {
            InputTokens = 10,
            OutputTokens = 5,
            CacheRead = 8,
            CacheWrite = 2
        };

        var json = System.Text.Json.JsonSerializer.Serialize(payload);

        Assert.Contains("\"inputTokens\"", json);
        Assert.Contains("\"outputTokens\"", json);
        Assert.Contains("\"cacheRead\"", json);
        Assert.Contains("\"cacheWrite\"", json);
    }
}
