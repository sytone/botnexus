using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ExpandedComposerTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly IAgentInteractionService _interaction = Substitute.For<IAgentInteractionService>();

    public ExpandedComposerTests()
    {
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(new SlashCommandDispatcher(_interaction));
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        var preferences = Substitute.For<IPortalPreferencesService>();
        preferences.Current.Returns(new PortalPreferences());
        _ctx.Services.AddSingleton(preferences);
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        _store.UpsertAgent(new AgentState { AgentId = "agent-1", DisplayName = "Agent", IsConnected = true });
        _store.SeedConversations("agent-1",
        [
            Conversation("conv-1", isDefault: true),
            Conversation("conv-2", isDefault: false)
        ]);
        _store.SetActiveConversation("agent-1", "conv-1");
    }

    public void Dispose() => _ctx.Dispose();

    private static ConversationSummaryDto Conversation(string id, bool isDefault) => new(
        id, "agent-1", id, isDefault, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private IRenderedComponent<ChatPanel> Render(string conversationId = "conv-1") =>
        _ctx.Render<ChatPanel>(p => p
            .Add(c => c.AgentId, "agent-1")
            .Add(c => c.ConversationId, conversationId));

    [Fact]
    public async Task Expanded_composer_edits_one_shared_draft_and_enter_never_dispatches()
    {
        var cut = Render();
        cut.Find("[data-testid=chat-input]").Input("first line");
        cut.Find("[data-testid=chat-expand]").Click();

        var expanded = cut.Find("[data-testid=expanded-composer-input]");
        expanded.Input("first line\nsecond line");
        expanded.KeyDown(new KeyboardEventArgs { Key = "Enter" });
        expanded.KeyDown(new KeyboardEventArgs { Key = "Enter", ShiftKey = true });

        await _interaction.DidNotReceiveWithAnyArgs().DeliverMessageAsync(default!, default!, default!);
        cut.Find("[data-testid=expanded-composer-close]").Click();
        cut.Find("[data-testid=chat-input]").GetAttribute("value").ShouldBe("first line\nsecond line");

        cut.Find("[data-testid=chat-expand]").Click();
        cut.Find("[data-testid=expanded-composer-input]").GetAttribute("value").ShouldBe("first line\nsecond line");
    }

    [Fact]
    public async Task Failed_submit_keeps_dialog_text_and_attachments_for_retry()
    {
        _interaction.DeliverMessageAsync(
                "agent-1", "conv-1", "retry me", InboundDeliveryMode.Auto,
                Arg.Any<IReadOnlyList<DraftAttachment>>())
            .Returns(Task.FromException(new IOException("delivery failed")));
        var cut = Render();
        cut.Find("[data-testid=chat-input]").Input("retry me");
        await cut.InvokeAsync(() => cut.Instance.AddDraftAttachmentsAsync(
            [new DraftAttachment("notes.txt", "text/plain", "aGk=", 2)]));
        cut.Find("[data-testid=chat-expand]").Click();

        await cut.InvokeAsync(() => cut.Find("[data-testid=expanded-composer-send]").Click());

        cut.Find("[data-testid=expanded-composer]");
        cut.Find("[data-testid=expanded-composer-error]").TextContent.ShouldContain("delivery failed");
        cut.Find("[data-testid=expanded-composer-input]").GetAttribute("value").ShouldBe("retry me");
        cut.FindAll("[data-testid=expanded-attachment-chip]").Count.ShouldBe(1);
    }

    [Fact]
    public void Ambient_conversation_keeps_inline_draft_when_expanded()
    {
        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));
        cut.Find("[data-testid=chat-input]").Input("ambient draft");

        cut.Find("[data-testid=chat-expand]").Click();

        cut.Find("[data-testid=expanded-composer-input]").GetAttribute("value").ShouldBe("ambient draft");
    }

    [Fact]
    public async Task Successful_explicit_submit_uses_shared_delivery_and_closes_and_clears()
    {
        var cut = Render();
        cut.Find("[data-testid=chat-input]").Input("send explicitly");
        cut.Find("[data-testid=chat-expand]").Click();
        cut.Find("[data-testid=expanded-composer-send]").Click();

        await _interaction.Received(1).DeliverMessageAsync(
            "agent-1", "conv-1", "send explicitly", InboundDeliveryMode.Auto,
            Arg.Any<IReadOnlyList<DraftAttachment>>());
        cut.FindAll("[data-testid=expanded-composer]").ShouldBeEmpty();
        cut.Find("[data-testid=chat-input]").GetAttribute("value").ShouldBe(string.Empty);
    }

    [Fact]
    public void Escape_closes_without_submitting_or_losing_edits()
    {
        var cut = Render();
        cut.Find("[data-testid=chat-input]").Input("keep this draft");
        cut.Find("[data-testid=chat-expand]").Click();

        cut.Find("[data-testid=expanded-composer-overlay]").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        cut.FindAll("[data-testid=expanded-composer]").ShouldBeEmpty();
        cut.Find("[data-testid=chat-input]").GetAttribute("value").ShouldBe("keep this draft");
    }

    [Fact]
    public async Task In_flight_submit_cannot_clear_the_new_conversations_draft()
    {
        var delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _interaction.DeliverMessageAsync(
                "agent-1", "conv-1", "send one", InboundDeliveryMode.Auto,
                Arg.Any<IReadOnlyList<DraftAttachment>>())
            .Returns(delivery.Task);
        var cut = Render();
        cut.Find("[data-testid=chat-input]").Input("send one");
        var submit = cut.InvokeAsync(() => cut.Find("[data-testid=chat-send]").Click());

        cut.Render(p => p
            .Add(c => c.AgentId, "agent-1")
            .Add(c => c.ConversationId, "conv-2"));
        cut.Find("[data-testid=chat-input]").Input("draft for two");
        delivery.SetResult();
        await submit;

        cut.Find("[data-testid=chat-input]").GetAttribute("value").ShouldBe("draft for two");
    }

    [Fact]
    public async Task Conversation_switch_closes_dialog_and_keeps_each_draft_with_its_target()
    {
        var cut = Render();
        cut.Find("[data-testid=chat-input]").Input("draft for one");
        cut.Find("[data-testid=chat-expand]").Click();

        cut.Render(p => p
            .Add(c => c.AgentId, "agent-1")
            .Add(c => c.ConversationId, "conv-2"));

        cut.FindAll("[data-testid=expanded-composer]").ShouldBeEmpty();
        cut.Find("[data-testid=chat-input]").GetAttribute("value").ShouldBe(string.Empty);
        cut.Find("[data-testid=chat-input]").Input("draft for two");
        cut.Find("[data-testid=chat-send]").Click();
        await _interaction.Received(1).DeliverMessageAsync(
            "agent-1", "conv-2", "draft for two", InboundDeliveryMode.Auto,
            Arg.Any<IReadOnlyList<DraftAttachment>>());
        await _interaction.DidNotReceive().DeliverMessageAsync(
            "agent-1", "conv-2", "draft for one", InboundDeliveryMode.Auto,
            Arg.Any<IReadOnlyList<DraftAttachment>>());

        cut.Render(p => p
            .Add(c => c.AgentId, "agent-1")
            .Add(c => c.ConversationId, "conv-1"));
        cut.Find("[data-testid=chat-input]").GetAttribute("value").ShouldBe("draft for one");
    }
}
