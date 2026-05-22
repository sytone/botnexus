using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Regression tests for #472 -- conversation switch cross-routing bugs in ClientStateStore.
/// </summary>
public sealed class ConversationSwitchRegressionTests
{
    private static ClientStateStore CreateSeededStore()
    {
        var store = new ClientStateStore();
        store.SeedAgents([new AgentSummary("agent-1", "Agent One")]);
        store.SeedConversations("agent-1", [CreateConversation("conv-1", "agent-1", "Conv 1")]);
        store.SetActiveConversation("agent-1", "conv-1");
        return store;
    }

    [Fact]
    public void RegisterSession_DoesNotOverwriteExistingActiveSessionId_WhenSwitchingConversations()
    {
        // Regression guard for #472 (ClientStateStore fix).
        // conv-1 already has an established session. User switches to conv-2 (no session yet).
        // RegisterSession for sess-2 must stamp conv-2 and leave conv-1 untouched.
        var store = CreateSeededStore();
        var agent = store.GetAgent("agent-1")!;

        // Seed both conversations, then restore conv-1's session (SeedConversations resets state)
        store.SeedConversations("agent-1", [
            CreateConversation("conv-1", "agent-1", "Conv 1"),
            CreateConversation("conv-2", "agent-1", "Conv 2")
        ]);
        // Set conv-1 to have an existing session (after seed)
        agent.Conversations["conv-1"].ActiveSessionId = "sess-1";
        agent.Conversations["conv-2"].ActiveSessionId = null;
        store.SetActiveConversation("agent-1", "conv-2");

        store.RegisterSession("agent-1", "sess-2");

        Assert.Equal("sess-2", agent.Conversations["conv-2"].ActiveSessionId);
        // conv-1 must be untouched
        Assert.Equal("sess-1", agent.Conversations["conv-1"].ActiveSessionId);
    }

    [Fact]
    public void RegisterSession_DoesNotStamp_WhenActiveConversationAlreadyHasSession()
    {
        // Regression guard for #472.
        // If the active conversation already has an ActiveSessionId, a late RegisterSession
        // call with a different session ID must not overwrite it.
        var store = CreateSeededStore();
        var agent = store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"].ActiveSessionId = "sess-existing";
        store.SetActiveConversation("agent-1", "conv-1");

        store.RegisterSession("agent-1", "sess-late");

        // Must not overwrite
        Assert.Equal("sess-existing", agent.Conversations["conv-1"].ActiveSessionId);
    }

    private static ConversationSummaryDto CreateConversation(
        string conversationId, string agentId, string title) =>
        new(conversationId, agentId, title, false, "Active", null, 0,
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow);
}
