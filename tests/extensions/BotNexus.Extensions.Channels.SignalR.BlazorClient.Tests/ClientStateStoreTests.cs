using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ClientStateStoreTests
{
    [Fact]
    public void SeedAgents_adds_agents()
    {
        var store = new ClientStateStore();

        store.SeedAgents([
            new AgentSummary("a-1", "Alpha"),
            new AgentSummary("a-2", "Beta")
        ]);

        Assert.Equal(2, store.Agents.Count);
        Assert.Equal("Alpha", store.GetAgent("a-1")?.DisplayName);
        Assert.Equal("Beta", store.GetAgent("a-2")?.DisplayName);
    }

    [Fact]
    public void SeedAgents_updates_existing_agent()
    {
        var store = new ClientStateStore();
        store.SeedAgents([new AgentSummary("a-1", "Old")]);

        store.SeedAgents([new AgentSummary("a-1", "New")]);

        Assert.Equal("New", store.GetAgent("a-1")?.DisplayName);
    }

    [Fact]
    public void UpsertAgent_replaces_or_adds_agent()
    {
        var store = new ClientStateStore();

        store.UpsertAgent(new AgentState { AgentId = "a-1", DisplayName = "Agent", IsConnected = true });

        Assert.True(store.GetAgent("a-1")?.IsConnected);
    }

    [Fact]
    public void SeedConversations_populates_agent_conversations_and_selects_default()
    {
        var store = CreateSeededStore();

        store.SeedConversations("a-1", [
            CreateConversation("c-1", "a-1", "General", isDefault: false, updatedAt: DateTimeOffset.UtcNow.AddMinutes(-1)),
            CreateConversation("c-2", "a-1", "Default", isDefault: true, updatedAt: DateTimeOffset.UtcNow)
        ]);

        var agent = store.GetAgent("a-1");
        Assert.NotNull(agent);
        Assert.Equal(2, agent.Conversations.Count);
        Assert.Equal("c-2", agent.ActiveConversationId);
    }

    [Fact]
    public void SetActiveConversation_updates_agent_and_clears_conversation_unread()
    {
        var store = CreateSeededStore();
        store.SeedConversations("a-1", [CreateConversation("c-1", "a-1", "General")]);
        store.GetAgent("a-1")!.Conversations["c-1"].UnreadCount = 3;

        store.SetActiveConversation("a-1", "c-1");

        Assert.Equal("c-1", store.GetAgent("a-1")?.ActiveConversationId);
        Assert.Equal(0, store.GetConversation("c-1")?.UnreadCount);
    }

    [Fact]
    public void GetConversation_searches_all_agents()
    {
        var store = CreateSeededStore();
        store.SeedConversations("a-1", [CreateConversation("c-1", "a-1", "One")]);
        store.SeedConversations("a-2", [CreateConversation("c-2", "a-2", "Two")]);

        Assert.Equal("Two", store.GetConversation("c-2")?.Title);
    }

    [Fact]
    public void AppendMessage_adds_message_to_conversation()
    {
        var store = CreateConversationStore();

        store.AppendMessage("c-1", new ChatMessage("User", "hello", DateTimeOffset.UtcNow));

        Assert.Single(store.GetMessages("c-1"));
        Assert.Equal("hello", store.GetMessages("c-1")[0].Content);
    }

    [Fact]
    public void PrependMessages_inserts_messages_at_start()
    {
        var store = CreateConversationStore();
        store.AppendMessage("c-1", new ChatMessage("Assistant", "newer", DateTimeOffset.UtcNow));

        store.PrependMessages("c-1", [
            new ChatMessage("User", "older-1", DateTimeOffset.UtcNow.AddMinutes(-2)),
            new ChatMessage("Assistant", "older-2", DateTimeOffset.UtcNow.AddMinutes(-1))
        ]);

        Assert.Equal(3, store.GetMessages("c-1").Count);
        Assert.Equal("older-1", store.GetMessages("c-1")[0].Content);
        Assert.Equal("older-2", store.GetMessages("c-1")[1].Content);
        Assert.Equal("newer", store.GetMessages("c-1")[2].Content);
    }

    [Fact]
    public void ClearMessages_clears_messages_and_marks_history_not_loaded()
    {
        var store = CreateConversationStore();
        store.GetConversation("c-1")!.HistoryLoaded = true;
        store.AppendMessage("c-1", new ChatMessage("User", "hello", DateTimeOffset.UtcNow));

        store.ClearMessages("c-1");

        Assert.Empty(store.GetMessages("c-1"));
        Assert.False(store.GetConversation("c-1")!.HistoryLoaded);
    }

    [Fact]
    public void SetStreaming_updates_conversation_and_agent_state()
    {
        var store = CreateConversationStore();

        store.SetStreaming("c-1", true);

        Assert.True(store.GetStreamState("c-1").IsStreaming);
        Assert.True(store.GetAgent("a-1")!.IsStreaming);
    }

    [Fact]
    public void AppendStreamBuffer_accumulates_delta_content()
    {
        var store = CreateConversationStore();

        store.AppendStreamBuffer("c-1", "hel");
        store.AppendStreamBuffer("c-1", "lo");

        Assert.Equal("hello", store.GetStreamState("c-1").Buffer);
    }

    [Fact]
    public void CommitStreamBuffer_appends_assistant_message_and_resets_stream_state()
    {
        var store = CreateConversationStore();
        store.SetStreaming("c-1", true);
        store.AppendStreamBuffer("c-1", "hello");
        store.GetStreamState("c-1").ThinkingBuffer = "thinking";

        store.CommitStreamBuffer("c-1");

        var messages = store.GetMessages("c-1");
        Assert.Single(messages);
        Assert.Equal("Assistant", messages[0].Role);
        Assert.Equal("hello", messages[0].Content);
        Assert.Equal("thinking", messages[0].ThinkingContent);
        Assert.False(store.GetStreamState("c-1").IsStreaming);
        Assert.Equal(string.Empty, store.GetStreamState("c-1").Buffer);
        Assert.Equal(string.Empty, store.GetStreamState("c-1").ThinkingBuffer);
    }

    [Fact]
    public void OnChanged_fires_for_mutations()
    {
        var store = CreateConversationStore();
        var count = 0;
        store.OnChanged += () => count++;

        store.AppendMessage("c-1", new ChatMessage("User", "hello", DateTimeOffset.UtcNow));
        store.SetStreaming("c-1", true);
        store.AppendStreamBuffer("c-1", "x");

        Assert.Equal(3, count);
    }

    [Fact]
    public void ActiveConversationId_returns_active_conversation_for_active_agent()
    {
        var store = CreateSeededStore();
        store.SeedConversations("a-1", [CreateConversation("c-1", "a-1", "One")]);
        store.ActiveAgentId = "a-1";
        store.SetActiveConversation("a-1", "c-1");

        Assert.Equal("c-1", store.ActiveConversationId);
    }

    private static ClientStateStore CreateSeededStore()
    {
        var store = new ClientStateStore();
        store.SeedAgents([
            new AgentSummary("a-1", "Alpha"),
            new AgentSummary("a-2", "Beta")
        ]);
        return store;
    }

    private static ClientStateStore CreateConversationStore()
    {
        var store = CreateSeededStore();
        store.SeedConversations("a-1", [CreateConversation("c-1", "a-1", "General")]);
        return store;
    }

    private static ConversationSummaryDto CreateConversation(
        string conversationId,
        string agentId,
        string title,
        bool isDefault = false,
        DateTimeOffset? updatedAt = null) =>
        new(
            conversationId,
            agentId,
            title,
            isDefault,
            "Active",
            null,
            0,
            DateTimeOffset.UtcNow.AddHours(-1),
            updatedAt ?? DateTimeOffset.UtcNow);
}
