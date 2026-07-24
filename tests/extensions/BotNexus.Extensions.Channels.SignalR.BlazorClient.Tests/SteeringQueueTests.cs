using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class SteeringQueueTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store;
    private readonly IAgentInteractionService _interaction;

    public SteeringQueueTests()
    {
        _store = new ClientStateStore();
        _interaction = Substitute.For<IAgentInteractionService>();

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private (AgentState agent, ConversationState conv) SetupAgentWithConversation(
        string agentId = "agent-1", string convId = "conv-1")
    {
        var agent = new AgentState
        {
            AgentId = agentId,
            DisplayName = "Test Agent",
            IsConnected = true
        };
        _store.UpsertAgent(agent);
        _store.SeedConversations(agentId, new[]
        {
            new ConversationSummaryDto(
                ConversationId: convId,
                AgentId: agentId,
                Title: "Test Conv",
                IsDefault: true,
                Status: "Active",
                ActiveSessionId: "session-1",
                BindingCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow)
        });
        _store.SelectView(agentId, string.Empty, SelectionSource.UserClick);
        var conv = _store.GetConversation(convId)!;
        return (agent, conv);
    }

    [Fact]
    public void Panel_hidden_when_queue_is_empty()
    {
        SetupAgentWithConversation();

        var cut = _ctx.Render<SteeringQueuePanel>(p => p.Add(c => c.ConversationId, "conv-1"));

        Assert.DoesNotContain("steering-queue-panel", cut.Markup);
    }

    [Fact]
    public void Adding_steering_entry_shows_it_in_panel()
    {
        SetupAgentWithConversation();
        var cut = _ctx.Render<SteeringQueuePanel>(p => p.Add(c => c.ConversationId, "conv-1"));

        var entry = new SteeringEntry("entry-1", "Focus on error handling", SteeringEntryKind.Steer, SteeringEntryStatus.Pending);
        _store.AddSteeringEntry("conv-1", entry);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("steering-queue-panel", cut.Markup);
            Assert.Contains("Focus on error handling", cut.Markup);
            Assert.Contains("Steer", cut.Markup);
        });
    }

    [Fact]
    public void Injected_status_removes_entry_from_panel()
    {
        SetupAgentWithConversation();
        var cut = _ctx.Render<SteeringQueuePanel>(p => p.Add(c => c.ConversationId, "conv-1"));

        var entry = new SteeringEntry("entry-1", "Fix the bug", SteeringEntryKind.Steer, SteeringEntryStatus.Pending);
        _store.AddSteeringEntry("conv-1", entry);

        cut.WaitForAssertion(() => Assert.Contains("Fix the bug", cut.Markup));

        // Mark as injected
        _store.UpdateSteeringEntry("conv-1", "entry-1", SteeringEntryStatus.Injected);

        cut.WaitForAssertion(() => Assert.DoesNotContain("Fix the bug", cut.Markup));
    }

    [Fact]
    public void Per_conversation_isolation_shows_correct_queue()
    {
        var agent = new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Test Agent",
            IsConnected = true
        };
        _store.UpsertAgent(agent);
        _store.SeedConversations("agent-1", new[]
        {
            new ConversationSummaryDto("conv-1", "agent-1", "Conv 1", true, "Active", "session-1", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("conv-2", "agent-1", "Conv 2", false, "Active", "session-2", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        });
        _store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);

        // Add entries to different conversations
        _store.AddSteeringEntry("conv-1", new SteeringEntry("e1", "First conv steer", SteeringEntryKind.Steer, SteeringEntryStatus.Pending));
        _store.AddSteeringEntry("conv-2", new SteeringEntry("e2", "Second conv steer", SteeringEntryKind.Steer, SteeringEntryStatus.Pending));

        // Render panel for conv-1
        var cut1 = _ctx.Render<SteeringQueuePanel>(p => p.Add(c => c.ConversationId, "conv-1"));
        Assert.Contains("First conv steer", cut1.Markup);
        Assert.DoesNotContain("Second conv steer", cut1.Markup);

        // Render panel for conv-2
        var cut2 = _ctx.Render<SteeringQueuePanel>(p => p.Add(c => c.ConversationId, "conv-2"));
        Assert.Contains("Second conv steer", cut2.Markup);
        Assert.DoesNotContain("First conv steer", cut2.Markup);
    }

    [Fact]
    public void FollowUp_entries_show_different_badge_than_steer_entries()
    {
        SetupAgentWithConversation();
        var cut = _ctx.Render<SteeringQueuePanel>(p => p.Add(c => c.ConversationId, "conv-1"));

        _store.AddSteeringEntry("conv-1", new SteeringEntry("e1", "Steer text", SteeringEntryKind.Steer, SteeringEntryStatus.Pending));
        _store.AddSteeringEntry("conv-1", new SteeringEntry("e2", "FollowUp text", SteeringEntryKind.FollowUp, SteeringEntryStatus.Pending));

        cut.WaitForAssertion(() =>
        {
            var items = cut.FindAll("[data-testid='steering-queue-item']");
            Assert.Equal(2, items.Count);

            // First item should have steer badge
            Assert.Contains("steering-queue-badge steer", items[0].InnerHtml);
            Assert.Contains("Steer", items[0].InnerHtml);

            // Second item should have followup badge
            Assert.Contains("steering-queue-badge followup", items[1].InnerHtml);
            Assert.Contains("Follow-up", items[1].InnerHtml);
        });
    }

    [Fact]
    public void Dropped_status_shows_error_indicator()
    {
        SetupAgentWithConversation();
        var cut = _ctx.Render<SteeringQueuePanel>(p => p.Add(c => c.ConversationId, "conv-1"));

        // Add as pending then mark as dropped — but since Dropped removes it,
        // we need to verify via the store state or check the brief render
        var entry = new SteeringEntry("e1", "Will be dropped", SteeringEntryKind.Steer, SteeringEntryStatus.Dropped);
        // Directly add a Dropped entry to verify rendering
        var conv = _store.GetConversation("conv-1")!;
        conv.PendingSteeringQueue.Add(entry);
        _store.NotifyChanged();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("steering-dropped-icon", cut.Markup);
        });
    }

    [Fact]
    public void SteeringFeedback_Injected_removes_pending_entry_and_adds_stream_message()
    {
        var (agent, conv) = SetupAgentWithConversation();
        _store.RegisterSession("agent-1", "session-1");

        // Add a pending entry
        _store.AddSteeringEntry("conv-1", new SteeringEntry("e1", "Do this instead", SteeringEntryKind.Steer, SteeringEntryStatus.Pending));

        // Simulate SteeringFeedback.Injected via the event handler
        var handler = new GatewayEventHandler(_store, new GatewayHubConnection(), Microsoft.Extensions.Logging.Abstractions.NullLogger<GatewayEventHandler>.Instance);

        handler.HandleSteeringFeedback(new SteeringFeedbackPayload(
            AgentId: "agent-1",
            SessionId: "session-1",
            Kind: SteeringFeedbackKind.Injected,
            ConversationId: "conv-1"));

        // Queue should be empty
        var queue = _store.GetSteeringQueue("conv-1");
        Assert.Empty(queue);

        // Stream message should be added
        var lastMsg = conv.Messages.Last();
        Assert.Equal("System", lastMsg.Role);
        Assert.Contains("Steering injected", lastMsg.Content);

        handler.Dispose();
    }

    [Fact]
    public void SteeringFeedback_Queued_keeps_entry_pending()
    {
        var (agent, conv) = SetupAgentWithConversation();
        _store.RegisterSession("agent-1", "session-1");

        // Add a pending entry
        _store.AddSteeringEntry("conv-1", new SteeringEntry("e1", "Queue this", SteeringEntryKind.Steer, SteeringEntryStatus.Pending));

        var handler = new GatewayEventHandler(_store, new GatewayHubConnection(), Microsoft.Extensions.Logging.Abstractions.NullLogger<GatewayEventHandler>.Instance);

        handler.HandleSteeringFeedback(new SteeringFeedbackPayload(
            AgentId: "agent-1",
            SessionId: "session-1",
            Kind: SteeringFeedbackKind.Queued,
            ConversationId: "conv-1"));

        // Queue should still have the entry
        var queue = _store.GetSteeringQueue("conv-1");
        Assert.Single(queue);
        Assert.Equal(SteeringEntryStatus.Pending, queue[0].Status);

        // Info message should be added
        var lastMsg = conv.Messages.Last();
        Assert.Contains("queued", lastMsg.Content);

        handler.Dispose();
    }

    [Fact]
    public void Panel_shows_in_ChatPanel_when_entries_exist()
    {
        SetupAgentWithConversation();

        _store.AddSteeringEntry("conv-1", new SteeringEntry("e1", "Steer it", SteeringEntryKind.Steer, SteeringEntryStatus.Pending));

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("steering-queue-panel", cut.Markup);
            Assert.Contains("Steer it", cut.Markup);
        });
    }

    [Fact]
    public void Panel_not_rendered_in_ChatPanel_when_queue_empty()
    {
        SetupAgentWithConversation();

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.DoesNotContain("steering-queue-panel", cut.Markup);
    }
}
