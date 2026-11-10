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
    public void Overflow_trigger_opens_an_accessible_menu_in_the_browser_top_layer()
    {
        var cut = RenderMenu([new("rename", "Rename", () => Task.CompletedTask)]);

        cut.Find("[data-testid='conversation-actions-trigger']").Click();

        var overlay = cut.Find("[data-testid='conversation-action-overlay']");
        Assert.Equal("manual", overlay.GetAttribute("popover"));
        Assert.Equal("menu", cut.Find("[data-testid='conversation-action-menu']").GetAttribute("role"));
        Assert.Equal("menuitem", cut.Find("[data-action-id='rename']").GetAttribute("role"));
    }

    [Fact]
    public async Task Context_menu_and_keyboard_context_key_open_the_same_menu()
    {
        var cut = RenderMenu([new("rename", "Rename", () => Task.CompletedTask)], child: true);
        var target = cut.Find("[data-testid='conversation-action-context-target']");

        target.ContextMenu(new MouseEventArgs { ClientX = 31, ClientY = 42 });
        cut.Find("[data-testid='conversation-action-menu']");

        await cut.InvokeAsync(cut.Instance.Close);
        await cut.InvokeAsync(() => cut.Instance.OpenFromBrowser(false));
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
        await cut.InvokeAsync(() => cut.Instance.DismissFromBrowser("Escape", CurrentToken()));
        Assert.Empty(cut.FindAll("[data-testid='conversation-action-menu']"));

        trigger.Click();
        cut.Find("[data-testid='conversation-action-menu-dismiss']").Click();
        Assert.Empty(cut.FindAll("[data-testid='conversation-action-menu']"));

        trigger.Click();
        await cut.InvokeAsync(() => cut.Instance.DismissFromBrowser("scroll", CurrentToken()));
        Assert.Empty(cut.FindAll("[data-testid='conversation-action-menu']"));
        Assert.Contains(_ctx.JSInterop.Invocations, call => call.Identifier == "conversationActionMenu.focus");
    }

    [Fact]
    public async Task Native_click_is_the_only_action_activation_and_navigation_is_bound_to_actual_helper()
    {
        var invoked = string.Empty;
        var cut = RenderMenu([
            new("rename", "Rename", () => { invoked = "rename"; return Task.CompletedTask; }),
            new("pin", "Pin conversation", () => { invoked = "pin"; return Task.CompletedTask; })]);
        cut.Find("[data-testid='conversation-actions-trigger']").Click();
        var menu = cut.Find("[data-testid='conversation-action-menu']");

        // Native key handling is exercised by the actual-helper Node harness. bUnit does not
        // synthesize a browser's Enter/Space click; dispatch the one native click explicitly.
        _ctx.JSInterop.Invocations.ShouldContain(i => i.Identifier == "conversationActionMenu.bindTrigger");
        menu.QuerySelectorAll("[role=menuitem]").ShouldAllBe(i => i.GetAttribute("tabindex") == "-1");
        invoked.ShouldBeEmpty();
        await cut.Find("[data-action-id='pin']").ClickAsync(new());
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

    private string CurrentToken() => _ctx.JSInterop.Invocations.Last(i => i.Identifier == "conversationActionMenu.open")
        .Arguments[0].ShouldBeOfType<string>();

    [Fact]
    public async Task Reopen_UsesImmutableToken_AndStaleSupersededCallbackCannotCloseNewOpening()
    {
        var cut = RenderMenu([new("rename", "Rename", () => Task.CompletedTask)]);
        cut.Find("[data-testid='conversation-actions-trigger']").Click();
        var old = CurrentToken();
        await cut.InvokeAsync(() => cut.Instance.DismissFromBrowser("superseded", old));
        cut.Find("[data-testid='conversation-actions-trigger']").GetAttribute("aria-expanded").ShouldBe("false");
        _ctx.JSInterop.Invocations.ShouldNotContain(i => i.Identifier == "conversationActionMenu.close" || i.Identifier == "conversationActionMenu.focus");
        cut.Find("[data-testid='conversation-actions-trigger']").Click();
        var current = CurrentToken();
        current.ShouldNotBe(old);
        await cut.InvokeAsync(() => cut.Instance.DismissFromBrowser("resize", old));
        cut.Find("[data-testid='conversation-actions-trigger']").GetAttribute("aria-expanded").ShouldBe("true");
        await cut.InvokeAsync(cut.Instance.Close);
        _ctx.JSInterop.Invocations.Last(i => i.Identifier == "conversationActionMenu.close").Arguments[0].ShouldBe(current);
    }

    [Fact]
    public async Task TwoInstances_DisposingOldOwnerOnlyRetiresItsCapturedToken()
    {
        var a = RenderMenu([new("a", "A", () => Task.CompletedTask)]);
        var b = RenderMenu([new("b", "B", () => Task.CompletedTask)]);
        a.Find("[data-testid='conversation-actions-trigger']").Click();
        var old = CurrentToken();
        b.Find("[data-testid='conversation-actions-trigger']").Click();
        var current = CurrentToken();
        old.ShouldNotBe(current);
        await a.InvokeAsync(async () => await a.Instance.DisposeAsync());
        _ctx.JSInterop.Invocations.Last(i => i.Identifier == "conversationActionMenu.close").Arguments[0].ShouldBe(old);
        b.Find("[data-testid='conversation-actions-trigger']").GetAttribute("aria-expanded").ShouldBe("true");
    }

    [Fact]
    public async Task HeldOpen_DisposalDrainsOriginalContinuationWithoutPostOpenFocus()
    {
        var pending = _ctx.JSInterop.SetupVoid("conversationActionMenu.open", _ => true);
        var cut = _ctx.Render<ObservedRowMenu>(p => p
            .Add(c => c.SubjectAgentId, "agent-a").Add(c => c.SubjectConversationId, "conversation-a")
            .Add(c => c.Actions,
            new ConversationActionDefinition[] { new("a", "A", () => Task.CompletedTask) }));
        var click = cut.Find("[data-testid='conversation-actions-trigger']").ClickAsync(new());
        cut.WaitForAssertion(() => pending.Invocations.ShouldHaveSingleItem());
        var token = CurrentToken();
        await cut.InvokeAsync(async () => await cut.Instance.DisposeAsync());
        pending.SetVoidResult();
        await click;
        await cut.Instance.OpenRenderCompleted.Task;
        _ctx.JSInterop.Invocations.ShouldNotContain(i => i.Identifier == "conversationActionMenu.focusItem");
        _ctx.JSInterop.Invocations.Last(i => i.Identifier == "conversationActionMenu.close").Arguments[0].ShouldBe(token);
    }

    [Theory]
    [InlineData("conversationActionMenu.close")]
    [InlineData("conversationActionMenu.focus")]
    public async Task HeldAction_DisposalInvalidatesOriginalActionAndUsesItsOriginalToken(string identifier)
    {
        var calls = 0;
        var cut = RenderMenu([new("a", "A", () => { calls++; return Task.CompletedTask; })]);
        cut.Find("[data-testid='conversation-actions-trigger']").Click();
        var token = CurrentToken();
        var pending = _ctx.JSInterop.SetupVoid(identifier, _ => true);
        var click = cut.Find("[data-action-id=a]").ClickAsync(new());
        cut.WaitForAssertion(() => pending.Invocations.ShouldHaveSingleItem());
        pending.Invocations.Single().Arguments[0].ShouldBe(token);
        Task disposal = Task.CompletedTask;
        await cut.InvokeAsync(() => { disposal = cut.Instance.DisposeAsync().AsTask(); });
        // Synchronous disposal invalidation precedes the held owner's cleanup completion.
        pending.SetVoidResult();
        await disposal;
        await click;
        calls.ShouldBe(0);
        _ctx.JSInterop.Invocations.Where(i => i.Identifier == "conversationActionMenu.close")
            .ShouldAllBe(i => Equals(i.Arguments[0], token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RowAndSectionOverlap_BothDirectionsKeepRetirementArgumentsOwnerLocal(bool sectionFirst)
    {
        var row = RenderMenu([new("a", "A", () => Task.CompletedTask)]);
        var section = _ctx.Render<ConversationSectionActionMenu>(p => p.Add(c => c.CanMoveDown, true));
        string first;
        if (sectionFirst)
        {
            section.Find("[data-testid=section-actions-trigger]").Click();
            first = CurrentToken();
            row.Find("[data-testid='conversation-actions-trigger']").Click();
        }
        else
        {
            row.Find("[data-testid='conversation-actions-trigger']").Click();
            first = CurrentToken();
            section.Find("[data-testid=section-actions-trigger]").Click();
        }
        var second = CurrentToken();
        second.ShouldNotBe(first);
        if (sectionFirst)
        {
            await section.InvokeAsync(() => section.Instance.DismissFromBrowser("superseded", first));
            await section.InvokeAsync(async () => await section.Instance.DisposeAsync());
            row.Find("[data-testid='conversation-actions-trigger']").GetAttribute("aria-expanded").ShouldBe("true");
        }
        else
        {
            await row.InvokeAsync(() => row.Instance.DismissFromBrowser("superseded", first));
            await row.InvokeAsync(async () => await row.Instance.DisposeAsync());
            section.Find("[data-testid=section-actions-trigger]").GetAttribute("aria-expanded").ShouldBe("true");
        }
        _ctx.JSInterop.Invocations.Last(i => i.Identifier == "conversationActionMenu.close").Arguments[0].ShouldBe(first);
    }

    [Theory]
    [InlineData("conversationActionMenu.close", "permission")]
    [InlineData("conversationActionMenu.focus", "permission")]
    [InlineData("conversationActionMenu.close", "replacement")]
    [InlineData("conversationActionMenu.focus", "replacement")]
    [InlineData("conversationActionMenu.close", "permission-aba")]
    [InlineData("conversationActionMenu.focus", "permission-aba")]
    [InlineData("conversationActionMenu.close", "replacement-aba")]
    [InlineData("conversationActionMenu.focus", "replacement-aba")]
    public async Task HeldAction_SameRowAuthorizationChangesCancelOldClick(string identifier, string transition)
    {
        var oldCalls = 0;
        var replacementCalls = 0;
        ConversationActionDefinition[] original = [new("rename", "Rename", () => { oldCalls++; return Task.CompletedTask; })];
        var cut = RenderMenu(original);
        var instance = cut.Instance;
        cut.Find("[data-testid='conversation-actions-trigger']").Click();
        var token = CurrentToken();
        var pending = _ctx.JSInterop.SetupVoid(identifier, _ => true);
        var click = cut.Find("[data-action-id=rename]").ClickAsync(new());
        cut.WaitForAssertion(() => pending.Invocations.ShouldHaveSingleItem());
        pending.Invocations.Single().Arguments[0].ShouldBe(token);
        ConversationActionDefinition[] replacement = transition.StartsWith("permission", StringComparison.Ordinal)
            ? [] // Same factory result as a row becoming read-only.
            : [new("archive", "Archive", () => { replacementCalls++; return Task.CompletedTask; }, true)];
        cut.Render(p => p.Add(c => c.Actions, replacement));
        if (transition.EndsWith("-aba", StringComparison.Ordinal))
            cut.Render(p => p.Add(c => c.Actions, original));
        cut.Instance.ShouldBeSameAs(instance);
        pending.SetVoidResult();
        await click;
        oldCalls.ShouldBe(0);
        replacementCalls.ShouldBe(0);
        if (identifier == "conversationActionMenu.close")
            _ctx.JSInterop.Invocations.ShouldNotContain(i => i.Identifier == "conversationActionMenu.focus");
    }

    [Theory]
    [InlineData("conversationActionMenu.close")]
    [InlineData("conversationActionMenu.focus")]
    public async Task HeldAction_EquivalentRerenderRebuiltDelegatesUsesCurrentAuthorizedCallbackOnce(string identifier)
    {
        var oldCalls = 0;
        var currentCalls = 0;
        var cut = RenderMenu([new("rename", "Rename", () => { oldCalls++; return Task.CompletedTask; })]);
        cut.Find("[data-testid='conversation-actions-trigger']").Click();
        var pending = _ctx.JSInterop.SetupVoid(identifier, _ => true);
        var click = cut.Find("[data-action-id=rename]").ClickAsync(new());
        cut.WaitForAssertion(() => pending.Invocations.ShouldHaveSingleItem());
        // A normal parent render rebuilds closures. Delegate identity is not authorization.
        cut.Render(p => p.Add(c => c.SubjectAgentId, "agent-a")
            .Add(c => c.SubjectConversationId, "conversation-a")
            .Add(c => c.Actions, new ConversationActionDefinition[]
        {
            new("rename", "Rename", () => { currentCalls++; return Task.CompletedTask; })
        }));
        pending.SetVoidResult();
        await click;
        oldCalls.ShouldBe(0);
        currentCalls.ShouldBe(1);
    }

    [Theory]
    [InlineData("conversationActionMenu.close", "conversation")]
    [InlineData("conversationActionMenu.focus", "conversation")]
    [InlineData("conversationActionMenu.close", "agent")]
    [InlineData("conversationActionMenu.focus", "agent")]
    [InlineData("conversationActionMenu.close", "aba")]
    [InlineData("conversationActionMenu.focus", "aba")]
    [InlineData("conversationActionMenu.close", "missing")]
    [InlineData("conversationActionMenu.focus", "missing")]
    [InlineData("conversationActionMenu.close", "missing-aba")]
    [InlineData("conversationActionMenu.focus", "missing-aba")]
    public async Task HeldAction_SubjectTransitionWithIdenticalMetadataCancelsBothCallbacks(string identifier, string transition)
    {
        var oldCalls = 0;
        var newCalls = 0;
        ConversationActionDefinition[] original = [new("rename", "Rename", () => { oldCalls++; return Task.CompletedTask; })];
        var cut = RenderMenu(original);
        var instance = cut.Instance;
        cut.Find("[data-testid='conversation-actions-trigger']").Click();
        var token = CurrentToken();
        var pending = _ctx.JSInterop.SetupVoid(identifier, _ => true);
        var click = cut.Find("[data-action-id=rename]").ClickAsync(new());
        cut.WaitForAssertion(() => pending.Invocations.ShouldHaveSingleItem());
        var missing = transition.StartsWith("missing", StringComparison.Ordinal);
        cut.Render(p => p
            .Add(c => c.SubjectAgentId, transition == "agent" ? "agent-b" : "agent-a")
            .Add(c => c.SubjectConversationId, missing ? null : transition == "agent" ? "conversation-a" : "conversation-b")
            .Add(c => c.Actions, new ConversationActionDefinition[]
            {
                new("rename", "Rename", () => { newCalls++; return Task.CompletedTask; })
            }));
        if (transition is "aba" or "missing-aba")
            cut.Render(p => p.Add(c => c.SubjectAgentId, "agent-a")
                .Add(c => c.SubjectConversationId, "conversation-a").Add(c => c.Actions, original));
        cut.Instance.ShouldBeSameAs(instance);
        cut.FindAll("[data-testid='conversation-action-menu']").ShouldBeEmpty();
        pending.SetVoidResult();
        await click;
        oldCalls.ShouldBe(0);
        newCalls.ShouldBe(0);
        if (identifier == "conversationActionMenu.close")
            _ctx.JSInterop.Invocations.ShouldNotContain(i => i.Identifier == "conversationActionMenu.focus");
        _ctx.JSInterop.Invocations.Where(i => i.Identifier == "conversationActionMenu.close")
            .ShouldAllBe(i => Equals(i.Arguments[0], token));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "conversation-a")]
    [InlineData("agent-a", null)]
    [InlineData("", "conversation-a")]
    [InlineData("agent-a", " ")]
    public async Task MissingSubject_FailsClosedForAllEntryPoints(string? agentId, string? conversationId)
    {
        var calls = 0;
        var cut = _ctx.Render<ConversationActionMenu>(p => p
            .Add(c => c.SubjectAgentId, agentId).Add(c => c.SubjectConversationId, conversationId)
            .Add(c => c.Actions, new ConversationActionDefinition[]
            {
                new("rename", "Rename", () => { calls++; return Task.CompletedTask; })
            }));
        cut.FindAll("[data-testid='conversation-actions-trigger']").ShouldBeEmpty();
        cut.Find("[data-testid='conversation-action-context-target']").ContextMenu();
        await cut.InvokeAsync(() => cut.Instance.OpenFromBrowser(false));
        cut.FindAll("[data-testid='conversation-action-menu']").ShouldBeEmpty();
        calls.ShouldBe(0);
        _ctx.JSInterop.Invocations.ShouldNotContain(i => i.Identifier == "conversationActionMenu.open");

        // A later resolved initial subject can open normally; missing identity is not a permanent disable.
        cut.Render(p => p.Add(c => c.SubjectAgentId, "agent-a").Add(c => c.SubjectConversationId, "conversation-a"));
        cut.Find("[data-testid='conversation-actions-trigger']").Click();
        await cut.Find("[data-action-id=rename]").ClickAsync(new());
        calls.ShouldBe(1);
    }

    private sealed class ObservedRowMenu : ConversationActionMenu
    {
        private bool _observing;
        public ObservedRowMenu() { }
        public TaskCompletionSource<bool> OpenRenderCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            var observe = !firstRender && !_observing;
            if (observe) _observing = true;
            try { await base.OnAfterRenderAsync(firstRender); }
            finally { if (observe) OpenRenderCompleted.TrySetResult(true); }
        }
    }

    private IRenderedComponent<ConversationActionMenu> RenderMenu(
        IReadOnlyList<ConversationActionDefinition> actions,
        bool child = false) =>
        _ctx.Render<ConversationActionMenu>(parameters => parameters
            .Add(p => p.SubjectAgentId, "agent-a")
            .Add(p => p.SubjectConversationId, "conversation-a")
            .Add(p => p.Actions, actions)
            .Add(p => p.ChildContent, child
                ? builder => builder.AddMarkupContent(0, "<span>Conversation</span>")
                : null));
}
