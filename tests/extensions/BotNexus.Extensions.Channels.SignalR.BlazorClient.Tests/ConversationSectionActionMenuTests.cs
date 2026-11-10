using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>Source-only regressions for the bounded peer section menu; execution requires freeze lift.</summary>
public sealed class ConversationSectionActionMenuTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public ConversationSectionActionMenuTests() => _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Default_IsClosed_AndHasAnAccessibleNativeTrigger()
    {
        var cut = _ctx.Render<ConversationSectionActionMenu>();
        var trigger = cut.Find("[data-testid=section-actions-trigger]");
        trigger.TagName.ShouldBe("BUTTON");
        trigger.GetAttribute("aria-haspopup").ShouldBe("menu");
        trigger.GetAttribute("aria-expanded").ShouldBe("false");
        cut.FindAll("[role=menu]").ShouldBeEmpty();
    }

    [Fact]
    public void BuiltIn_OnlyOffersMoves_WithDisabledBoundaries()
    {
        var cut = _ctx.Render<ConversationSectionActionMenu>(p => p
            .Add(c => c.Label, "Pinned").Add(c => c.CanMoveDown, true));
        cut.Find("[data-testid=section-actions-trigger]").Click();
        cut.Find("[data-testid=section-move-up]").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("[data-testid=section-move-down]").HasAttribute("disabled").ShouldBeFalse();
        cut.FindAll("[data-testid=section-rename-btn], [data-testid=section-delete-btn]").ShouldBeEmpty();
        var label = Assert.IsType<string>(cut.Find("[role=menu]").GetAttribute("aria-label"));
        label.ShouldContain("Pinned");
    }

    [Fact]
    public void Custom_OffersRenameDelete_AndInvokesOneMove()
    {
        var moves = 0;
        var cut = _ctx.Render<ConversationSectionActionMenu>(p => p
            .Add(c => c.IsCustom, true).Add(c => c.CanMoveUp, true)
            .Add(c => c.OnMoveUp, () => moves++));
        cut.Find("[data-testid=section-actions-trigger]").Click();
        cut.Find("[data-testid=section-rename-btn]").TextContent.ShouldBe("Rename");
        cut.Find("[data-testid=section-delete-btn]").TextContent.ShouldBe("Delete section");
        cut.Find("[data-testid=section-move-up]").Click();
        moves.ShouldBe(1);
        cut.FindAll("[role=menu]").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Escape")]
    [InlineData("Tab")]
    public async Task KeyboardDismissal_ClosesWithoutInvokingAnAction(string key)
    {
        var calls = 0;
        var cut = _ctx.Render<ConversationSectionActionMenu>(p => p
            .Add(c => c.CanMoveDown, true).Add(c => c.OnMoveDown, () => calls++));
        await cut.InvokeAsync(() => cut.Instance.OpenFromBrowser(false));
        await cut.InvokeAsync(() => cut.Instance.DismissFromBrowser(key, CurrentToken()));
        cut.FindAll("[role=menu]").ShouldBeEmpty();
        calls.ShouldBe(0);
    }

    [Fact]
    public void UnsafeState_DisablesTrigger_AndClosesAnOpenMenu()
    {
        var cut = _ctx.Render<ConversationSectionActionMenu>();
        cut.Find("[data-testid=section-actions-trigger]").Click();
        cut.Render(p => p.Add(c => c.Disabled, true));
        cut.Find("[data-testid=section-actions-trigger]").HasAttribute("disabled").ShouldBeTrue();
        cut.FindAll("[role=menu]").ShouldBeEmpty();
    }

    [Fact]
    public void OutsideDismissal_ClosesPopoverAndRestoresTriggerFocus()
    {
        var cut = _ctx.Render<ConversationSectionActionMenu>();
        cut.Find("[data-testid=section-actions-trigger]").Click();
        cut.Find("[data-testid=section-menu-dismiss]").Click();
        cut.FindAll("[role=menu]").ShouldBeEmpty();
        _ctx.JSInterop.Invocations.ShouldContain(call => call.Identifier == "conversationActionMenu.close");
        _ctx.JSInterop.Invocations.ShouldContain(call => call.Identifier == "conversationActionMenu.focus");
    }
    [Theory]
    [InlineData("conversationActionMenu.close", "scope")]
    [InlineData("conversationActionMenu.focus", "scope-aba")]
    [InlineData("conversationActionMenu.close", "disabled-aba")]
    [InlineData("conversationActionMenu.focus", "disposed")]
    [InlineData("conversationActionMenu.close", "disposed")]
    public async Task HeldAction_InvalidatedLifetimeNeverInvokesOldCallback(string identifier, string transition)
    {
        var calls = 0;
        var cut = _ctx.Render<ConversationSectionActionMenu>(p => p.Add(c => c.Scope, "a")
            .Add(c => c.IsCustom, true).Add(c => c.OnDelete, () => calls++));
        cut.Find("[data-testid=section-actions-trigger]").Click();
        var pending = _ctx.JSInterop.SetupVoid(identifier, _ => true);
        var click = cut.Find("[data-testid=section-delete-btn]").ClickAsync(new());
        cut.WaitForAssertion(() => pending.Invocations.ShouldHaveSingleItem());
        pending.Invocations.Single().Arguments[0].ShouldBe(CurrentToken());
        if (transition == "disposed")
        {
            // Disposal invalidates synchronously and also retires the last issued opening,
            // even while its action-close is held. Drain both calls without awaiting the hold.
            Task disposal = Task.CompletedTask;
            await cut.InvokeAsync(() => { disposal = cut.Instance.DisposeAsync().AsTask(); });
            pending.SetVoidResult();
            await disposal;
        }
        else if (transition == "disabled-aba")
        {
            cut.Render(p => p.Add(c => c.Disabled, true));
            cut.Render(p => p.Add(c => c.Disabled, false));
        }
        else
        {
            cut.Render(p => p.Add(c => c.Scope, "b"));
            if (transition == "scope-aba") cut.Render(p => p.Add(c => c.Scope, "a"));
        }
        pending.SetVoidResult();
        await click;
        calls.ShouldBe(0);
        if (identifier == "conversationActionMenu.close")
            _ctx.JSInterop.Invocations.ShouldNotContain(i => i.Identifier == "conversationActionMenu.focus");
    }

    [Theory]
    [InlineData("conversationActionMenu.close")]
    [InlineData("conversationActionMenu.focus")]
    public async Task HeldAction_ParentScopeInvalidatesBeforeNewParametersArrive(string identifier)
    {
        var current = true;
        var calls = 0;
        var cut = _ctx.Render<ConversationSectionActionMenu>(p => p.Add(c => c.IsCustom, true)
            .Add(c => c.IsScopeCurrent, () => current).Add(c => c.OnDelete, () => calls++));
        cut.Find("[data-testid=section-actions-trigger]").Click();
        var pending = _ctx.JSInterop.SetupVoid(identifier, _ => true);
        var click = cut.Find("[data-testid=section-delete-btn]").ClickAsync(new());
        cut.WaitForAssertion(() => pending.Invocations.ShouldHaveSingleItem());
        pending.Invocations.Single().Arguments[0].ShouldBe(CurrentToken());
        current = false; // Synchronous parent invalidation, no child rerender/parameter delivery.
        pending.SetVoidResult();
        await click;
        calls.ShouldBe(0);
        if (identifier == "conversationActionMenu.close")
            _ctx.JSInterop.Invocations.ShouldNotContain(i => i.Identifier == "conversationActionMenu.focus");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldOpen_InvalidationPreventsPostOpenFocus(bool dispose)
    {
        var pending = _ctx.JSInterop.SetupVoid("conversationActionMenu.open", _ => true);
        var cut = _ctx.Render<ObservedActionMenu>(p => p.Add(c => c.Scope, "a").Add(c => c.CanMoveDown, true));
        var click = cut.Find("[data-testid=section-actions-trigger]").ClickAsync(new());
        cut.WaitForAssertion(() => pending.Invocations.ShouldHaveSingleItem());
        pending.Invocations.Single().Arguments[0].ShouldBe(CurrentToken());
        if (dispose)
            await cut.InvokeAsync(async () => await cut.Instance.DisposeAsync());
        else
            cut.Render(p => p.Add(c => c.Scope, "b"));
        pending.SetVoidResult();
        await click;
        // Event dispatch need not await OnAfterRenderAsync; observe the original render's
        // continuation explicitly before proving the absence of post-open focus.
        await cut.Instance.OpenRenderCompleted.Task;
        _ctx.JSInterop.Invocations.ShouldNotContain(i => i.Identifier == "conversationActionMenu.focusItem");
    }

    private string CurrentToken() => _ctx.JSInterop.Invocations.Last(i => i.Identifier == "conversationActionMenu.open")
        .Arguments[0].ShouldBeOfType<string>();

    [Fact]
    public async Task SupersededAndStaleCallbacks_OnlyClearTheirOwnOpeningWithoutGlobalCleanupOrFocus()
    {
        var cut = _ctx.Render<ConversationSectionActionMenu>(p => p.Add(c => c.CanMoveDown, true));
        cut.Find("[data-testid=section-actions-trigger]").Click();
        var first = CurrentToken();
        await cut.InvokeAsync(() => cut.Instance.DismissFromBrowser("superseded", first));
        cut.Find("[data-testid=section-actions-trigger]").GetAttribute("aria-expanded").ShouldBe("false");
        _ctx.JSInterop.Invocations.ShouldNotContain(i => i.Identifier == "conversationActionMenu.close" || i.Identifier == "conversationActionMenu.focus");
        cut.Find("[data-testid=section-actions-trigger]").Click();
        var second = CurrentToken();
        second.ShouldNotBe(first);
        await cut.InvokeAsync(() => cut.Instance.DismissFromBrowser("Escape", first));
        cut.Find("[data-testid=section-actions-trigger]").GetAttribute("aria-expanded").ShouldBe("true");
        cut.Find("[data-testid=section-menu-dismiss]").Click();
        _ctx.JSInterop.Invocations.Last(i => i.Identifier == "conversationActionMenu.close").Arguments[0].ShouldBe(second);
        _ctx.JSInterop.Invocations.Last(i => i.Identifier == "conversationActionMenu.focus").Arguments[0].ShouldBe(second);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("scope-aba")]
    [InlineData("disabled-aba")]
    public async Task ClosedOpening_ParameterInvalidationRevokesFocusBeforeDelayedBrowserDelivery(string transition)
    {
        var browser = new DelayedFocusBrowser();
        _ctx.Services.AddSingleton<IJSRuntime>(browser);
        var calls = 0;
        var cut = _ctx.Render<ConversationSectionActionMenu>(p => p.Add(c => c.Scope, "a")
            .Add(c => c.IsCustom, true).Add(c => c.OnDelete, () => calls++));
        cut.Find("[data-testid=section-actions-trigger]").Click();
        var token = browser.OpenToken ?? throw new InvalidOperationException("Browser opening was not issued.");
        token.ShouldNotBeNull();
        await cut.Find("[data-testid=section-delete-btn]").ClickAsync(new());
        cut.FindAll("[role=menu]").ShouldBeEmpty();
        calls.ShouldBe(1);
        browser.FocusDeliveries.Count.ShouldBe(1);
        browser.Permit.ShouldBe(token); // Browser close executed, focus delivery has NOT executed.
        if (transition == "disabled-aba")
        {
            cut.Render(p => p.Add(c => c.Disabled, true));
            cut.Render(p => p.Add(c => c.Disabled, false));
        }
        else
        {
            cut.Render(p => p.Add(c => c.Scope, "b"));
            if (transition == "scope-aba") cut.Render(p => p.Add(c => c.Scope, "a"));
        }
        cut.WaitForAssertion(() => browser.Revoked.ShouldContain(token));
        browser.Permit.ShouldBeNull();
        browser.DeliverFocus(); // Simulates JS invocation delivery/execution AFTER invalidation/ABA.
        browser.FocusWrites.ShouldBe(0);
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task ClosedOpening_EquivalentRerenderAllowsOneDelayedBrowserFocus()
    {
        var browser = new DelayedFocusBrowser();
        _ctx.Services.AddSingleton<IJSRuntime>(browser);
        var cut = _ctx.Render<ConversationSectionActionMenu>(p => p.Add(c => c.Scope, "a"));
        cut.Find("[data-testid=section-actions-trigger]").Click();
        await cut.Find("[data-testid=section-menu-dismiss]").ClickAsync(new());
        cut.Render(p => p.Add(c => c.Label, "Equivalent new label"));
        browser.DeliverFocus();
        browser.FocusWrites.ShouldBe(1);
        browser.Permit.ShouldBeNull();
    }

    // Delivery model, not an actual JS engine. The companion actual-helper harness separately
    // exercises the same queued-focus ordering against production JS. No .NET completion is held.
    private sealed class DelayedFocusBrowser : IJSRuntime
    {
        public string? OpenToken { get; private set; }
        public string? Permit { get; private set; }
        public List<string> Revoked { get; } = [];
        public Queue<string> FocusDeliveries { get; } = new();
        public int FocusWrites { get; private set; }
        private string? _active;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "conversationActionMenu.open")
            {
                OpenToken = args?.ElementAtOrDefault(0) as string;
                _active = OpenToken;
                Permit = null;
            }
            else if (identifier == "conversationActionMenu.close" && args?.ElementAtOrDefault(0) is string token)
            {
                var restore = args.ElementAtOrDefault(2) is true;
                if (!restore)
                {
                    Revoked.Add(token);
                    if (Permit == token) Permit = null;
                }
                if (_active == token)
                {
                    _active = null;
                    Permit = restore ? token : null;
                }
            }
            else if (identifier == "conversationActionMenu.focus" && args?.ElementAtOrDefault(0) is string focusToken)
                FocusDeliveries.Enqueue(focusToken);
            return default;
        }

        public void DeliverFocus()
        {
            while (FocusDeliveries.TryDequeue(out var token))
            {
                if (Permit != token || _active is not null) continue;
                Permit = null;
                ++FocusWrites;
            }
        }
    }

    private sealed class ObservedActionMenu : ConversationSectionActionMenu
    {
        private bool _observing;
        public ObservedActionMenu() { }
        public TaskCompletionSource<bool> OpenRenderCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            var observe = !firstRender && !_observing;
            if (observe) _observing = true;
            try { await base.OnAfterRenderAsync(firstRender); }
            finally { if (observe) OpenRenderCompleted.TrySetResult(true); }
        }
    }
}

