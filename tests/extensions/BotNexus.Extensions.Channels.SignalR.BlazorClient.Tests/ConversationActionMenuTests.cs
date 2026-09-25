using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using Microsoft.AspNetCore.Components.Web;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ConversationActionMenuTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public ConversationActionMenuTests() => _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Overflow_trigger_opens_an_accessible_menu()
    {
        var cut = RenderMenu([new("rename", "Rename", () => Task.CompletedTask)]);

        cut.Find("[data-testid='conversation-actions-trigger']").Click();

        Assert.Equal("menu", cut.Find("[data-testid='conversation-action-menu']").GetAttribute("role"));
        Assert.Equal("menuitem", cut.Find("[data-action-id='rename']").GetAttribute("role"));
    }

    [Fact]
    public void Context_menu_and_keyboard_context_key_open_the_same_menu()
    {
        var cut = RenderMenu([new("rename", "Rename", () => Task.CompletedTask)], child: true);
        var target = cut.Find("[data-testid='conversation-action-context-target']");

        target.ContextMenu(new MouseEventArgs { ClientX = 31, ClientY = 42 });
        cut.Find("[data-testid='conversation-action-menu']");

        cut.Instance.Close();
        target.KeyDown(new KeyboardEventArgs { Key = "ContextMenu" });
        cut.Find("[data-testid='conversation-action-menu']");
    }

    [Fact]
    public async Task Invoking_an_action_closes_the_menu_and_calls_it_once()
    {
        var calls = 0;
        var cut = RenderMenu([new("pin", "Pin conversation", () => { calls++; return Task.CompletedTask; })]);
        cut.Find("[data-testid='conversation-actions-trigger']").Click();

        await cut.Find("[data-action-id='pin']").ClickAsync(new MouseEventArgs());

        Assert.Equal(1, calls);
        Assert.Empty(cut.FindAll("[data-testid='conversation-action-menu']"));
    }

    [Fact]
    public async Task Escape_outside_click_and_scroll_dismiss_and_return_focus()
    {
        var cut = RenderMenu([new("rename", "Rename", () => Task.CompletedTask)]);
        var trigger = cut.Find("[data-testid='conversation-actions-trigger']");
        trigger.Click();
        cut.Find("[data-testid='conversation-action-menu']").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(cut.FindAll("[data-testid='conversation-action-menu']"));

        trigger.Click();
        cut.Find("[data-testid='conversation-action-menu-dismiss']").Click();
        Assert.Empty(cut.FindAll("[data-testid='conversation-action-menu']"));

        trigger.Click();
        await cut.InvokeAsync(() => cut.Instance.DismissFromBrowser("scroll"));
        Assert.Empty(cut.FindAll("[data-testid='conversation-action-menu']"));
        Assert.Contains(_ctx.JSInterop.Invocations, call => call.Identifier == "conversationActionMenu.focus");
    }

    [Fact]
    public void Arrow_keys_roam_and_enter_invokes_the_focused_item()
    {
        var invoked = string.Empty;
        var cut = RenderMenu([
            new("rename", "Rename", () => { invoked = "rename"; return Task.CompletedTask; }),
            new("pin", "Pin conversation", () => { invoked = "pin"; return Task.CompletedTask; })]);
        cut.Find("[data-testid='conversation-actions-trigger']").Click();
        var menu = cut.Find("[data-testid='conversation-action-menu']");

        menu.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        menu.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.Equal("pin", invoked);
    }


    [Fact]
    public void Sidebar_and_chat_header_render_the_shared_overflow_entry_point()
    {
        var sidebar = RenderMenu([new("pin", "Pin", () => Task.CompletedTask)], child: true);
        var header = RenderMenu([new("rename", "Rename", () => Task.CompletedTask)]);

        Assert.Single(sidebar.FindAll("[data-testid='conversation-actions-trigger']"));
        Assert.Single(header.FindAll("[data-testid='conversation-actions-trigger']"));
    }

    [Fact]
    public void Action_factory_filters_read_only_default_and_virtual_conversations()
    {
        var callbacks = ConversationActionCallbacks.NoOp;

        Assert.Empty(ConversationActionDefinitions.Create(new(false, false, false, false, false, []), callbacks));
        Assert.Equal(["rename"], ConversationActionDefinitions.Create(new(true, true, false, false, false, []), callbacks).Select(a => a.Id));
        Assert.Empty(ConversationActionDefinitions.Create(new(true, false, true, true, true, []), callbacks));
        Assert.Equal("Close", ConversationActionDefinitions.Create(new(true, false, false, true, true, []), callbacks).Single(a => a.Id == "archive").Label);
    }

    private IRenderedComponent<ConversationActionMenu> RenderMenu(
        IReadOnlyList<ConversationActionDefinition> actions,
        bool child = false) =>
        _ctx.Render<ConversationActionMenu>(parameters => parameters
            .Add(p => p.Actions, actions)
            .Add(p => p.ChildContent, child
                ? builder => builder.AddMarkupContent(0, "<span>Conversation</span>")
                : null));
}
