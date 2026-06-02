using BotNexus.Integration.E2E.Tests.PageObjects;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Mobile Blazor client tests — covers the /mobile/ path served by the gateway.
/// Verifies scroll behaviour, error bar UX, and the core chat flow on a narrow viewport.
///
/// Issues covered:
///   #722 — chatScroll.js not loaded in index.html → auto-scroll completely broken
///   #723 — blazor-error-ui undismissable / no error detail on mobile
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class MobileChatTests
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _output;

    // Mobile viewport: iPhone 14 Pro dimensions
    private const int MobileWidth = 390;
    private const int MobileHeight = 844;

    public MobileChatTests(NewUserExperienceFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _output = output;
    }

    private async Task<(IBrowser? browser, IPage? page, MobilePortalPage? mobilePage)>
        TryLaunchMobileAsync(IPlaywright playwright)
    {
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        if (browser is null)
            return (null, null, null);

        var ctx = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = MobileWidth, Height = MobileHeight },
            UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15",
            IsMobile = true,
            HasTouch = true
        });

        var page = await ctx.NewPageAsync();
        page.SetDefaultTimeout(15_000);
        var mobilePage = new MobilePortalPage(page);
        return (browser, page, mobilePage);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Static asset regression tests (do not require gateway connection)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Regression for #722: chatScroll.js MUST be referenced in index.html.
    /// Without it every JS.InvokeVoidAsync("chatScroll.*") silently fails and
    /// auto-scroll never fires.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Mobile")]
    [Trait("Area", "Assets")]
    public async Task MobileIndexHtml_MustLoadChatScrollJs()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;

        var ctx = await browser!.NewContextAsync();
        var page = await ctx.NewPageAsync();

        var baseUrl = _fx.GatewayBaseUrl.TrimEnd('/');
        var response = await page.GotoAsync($"{baseUrl}/mobile/index.html",
            new PageGotoOptions { WaitUntil = WaitUntilState.Commit });

        Assert.NotNull(response);
        Assert.True(response!.Ok, $"mobile/index.html must load successfully, got {response.Status}");

        var content = await response.TextAsync();

        // chatScroll.js must be in the script list
        Assert.Contains("chatScroll.js", content);

        // Verify ordering: chatScroll must appear BEFORE blazor.webassembly.js
        var scrollPos = content.IndexOf("chatScroll.js", StringComparison.Ordinal);
        var blazorPos = content.IndexOf("blazor.webassembly.js", StringComparison.Ordinal);
        Assert.True(scrollPos < blazorPos,
            "chatScroll.js must be loaded before blazor.webassembly.js so it is available " +
            "when Blazor initialises. Missing script tag = issue #722.");
    }

    /// <summary>
    /// chatScroll.js must be fetchable at its declared path and define the required functions.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Mobile")]
    [Trait("Area", "Assets")]
    public async Task MobileChatScrollJs_MustBeServableAndDefineRequiredFunctions()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;

        var ctx = await browser!.NewContextAsync();
        var page = await ctx.NewPageAsync();

        var baseUrl = _fx.GatewayBaseUrl.TrimEnd('/');
        var response = await page.GotoAsync($"{baseUrl}/mobile/js/chatScroll.js",
            new PageGotoOptions { WaitUntil = WaitUntilState.Commit });

        Assert.NotNull(response);
        Assert.True(response!.Ok, $"chatScroll.js must be fetchable at /mobile/js/chatScroll.js, got {response.Status}");

        var content = await response.TextAsync();
        Assert.Contains("scrollToBottom", content);
        Assert.Contains("forceScrollToBottom", content);
    }

    /// <summary>
    /// Regression for #723: The #blazor-error-ui element must have a dismiss button.
    /// On mobile there is no DevTools, so the user cannot see the error or dismiss the bar.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Mobile")]
    [Trait("Area", "ErrorUI")]
    public async Task MobileIndexHtml_ErrorUi_MustHaveDismissButton()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;

        var ctx = await browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = MobileWidth, Height = MobileHeight },
            IsMobile = true
        });
        var page = await ctx.NewPageAsync();

        var baseUrl = _fx.GatewayBaseUrl.TrimEnd('/');
        await page.GotoAsync($"{baseUrl}/mobile/index.html",
            new PageGotoOptions { WaitUntil = WaitUntilState.Commit });

        var content = await page.ContentAsync();
        Assert.Contains("blazor-error-ui", content);

        var errorDiv = page.Locator("#blazor-error-ui");
        await errorDiv.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });

        // Must contain a dismiss/close control
        var dismissBtn = errorDiv.Locator("button, [data-dismiss], [aria-label*='dismiss' i], [aria-label*='close' i]");
        var count = await dismissBtn.CountAsync();

        Assert.True(count > 0,
            "#blazor-error-ui must contain a dismiss/close button on mobile since users " +
            "cannot open DevTools to investigate. See issue #723.");
    }

    /// <summary>
    /// When the mobile page loads, window.chatScroll must be defined and have the
    /// required functions so that Blazor JS interop succeeds.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Mobile")]
    [Trait("Area", "Assets")]
    public async Task MobilePage_ChatScrollObject_DefinedAfterLoad()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, page, mobilePage) = await TryLaunchMobileAsync(playwright);
        Skip.If(browser is null, "Browser not available");
        await using var _ = browser!;

        await mobilePage!.NavigateAsync(_fx.GatewayBaseUrl);

        // chatScroll must be defined before Blazor boots
        var defined = await page!.EvaluateAsync<bool>("() => typeof window.chatScroll !== 'undefined'");
        Assert.True(defined,
            "window.chatScroll must be defined after mobile page loads. " +
            "Fails when chatScroll.js is missing from index.html (issue #722).");

        var hasScrollToBottom = await page.EvaluateAsync<bool>(
            "() => typeof window.chatScroll?.scrollToBottom === 'function'");
        Assert.True(hasScrollToBottom, "chatScroll.scrollToBottom must be a function");

        var hasForceScroll = await page.EvaluateAsync<bool>(
            "() => typeof window.chatScroll?.forceScrollToBottom === 'function'");
        Assert.True(hasForceScroll, "chatScroll.forceScrollToBottom must be a function");

        _output.WriteLine("window.chatScroll is defined with required functions");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Live portal tests
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mobile portal loads, shows agent selector populated with at least one agent.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Mobile")]
    [Trait("Area", "Load")]
    public async Task MobilePortal_LoadsAndShowsAgentSelector()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, page, mobilePage) = await TryLaunchMobileAsync(playwright);
        Skip.If(browser is null, "Browser not available");
        await using var _ = browser!;

        await mobilePage!.NavigateAsync(_fx.GatewayBaseUrl);
        await mobilePage.WaitForReadyAsync();

        var optionCount = await mobilePage.AgentSelect.Locator("option").CountAsync();
        Assert.True(optionCount > 0, "Agent selector must have at least one agent after portal load");

        _output.WriteLine($"Mobile portal loaded with {optionCount} agent(s)");
    }

    /// <summary>
    /// After portal load, chatScroll.forceScrollToBottom is called — meaning the JS
    /// is loaded and scroll-to-bottom actually fires on conversation selection.
    /// Regression test for #722.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Mobile")]
    [Trait("Area", "Scroll")]
    public async Task MobilePortal_OnConversationSelect_CallsForceScrollToBottom()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, page, mobilePage) = await TryLaunchMobileAsync(playwright);
        Skip.If(browser is null, "Browser not available");
        await using var _ = browser!;

        await mobilePage!.NavigateAsync(_fx.GatewayBaseUrl);
        await mobilePage.WaitForReadyAsync();

        // Instrument forceScrollToBottom before triggering scroll
        await page!.EvaluateAsync(@"() => {
            window._forceScrollCount = 0;
            const orig = window.chatScroll?.forceScrollToBottom;
            if (orig) {
                window.chatScroll.forceScrollToBottom = function(el) {
                    window._forceScrollCount++;
                    return orig.call(this, el);
                };
            }
        }");

        // Select first conversation to trigger ConditionalScrollAsync → ForceScrollToBottomAsync
        var convOptions = mobilePage.ConvSelect.Locator("option");
        var firstConv = await convOptions.First.GetAttributeAsync("value");
        if (firstConv is not null)
        {
            await mobilePage.ConvSelect.SelectOptionAsync(firstConv);
            await page.WaitForTimeoutAsync(600);
        }

        var callCount = await page.EvaluateAsync<int>("() => window._forceScrollCount ?? 0");
        Assert.True(callCount > 0,
            "chatScroll.forceScrollToBottom must be called when a conversation is selected. " +
            "If 0, chatScroll.js is not loaded (issue #722).");
    }

    /// <summary>
    /// Sending a message triggers streaming scroll calls — scrollToBottom(el, true) fires
    /// during streaming so the user sees the latest token.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Mobile")]
    [Trait("Area", "Scroll")]
    public async Task MobilePortal_SendMessage_ScrollsToLatestDuringStreaming()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, page, mobilePage) = await TryLaunchMobileAsync(playwright);
        Skip.If(browser is null, "Browser not available");
        await using var _ = browser!;

        await mobilePage!.NavigateAsync(_fx.GatewayBaseUrl);
        await mobilePage.WaitForReadyAsync();

        await page!.EvaluateAsync(@"() => {
            window._streamScrollCount = 0;
            const orig = window.chatScroll?.scrollToBottom;
            if (orig) {
                window.chatScroll.scrollToBottom = function(el, isStreaming) {
                    if (isStreaming) window._streamScrollCount++;
                    return orig.call(this, el, isStreaming);
                };
            }
        }");

        await mobilePage.SendMessageAsync("HELLO_WORLD");

        // Wait for streaming to complete
        try
        {
            await mobilePage.WaitForStreamingCompleteAsync(30_000);
        }
        catch { /* stream may not appear if mock responds instantly */ }

        await page.WaitForTimeoutAsync(500);

        var streamScrollCount = await page.EvaluateAsync<int>("() => window._streamScrollCount ?? 0");
        Assert.True(streamScrollCount > 0,
            "chatScroll.scrollToBottom(el, true) must fire during streaming. " +
            "Count 0 = chatScroll.js missing from index.html (issue #722).");

        // After stream completes, must be scrolled to bottom
        var atBottom = await mobilePage.IsScrolledToBottomAsync();
        Assert.True(atBottom, "Message stream must be at bottom after streaming completes");
    }

    /// <summary>
    /// When user has scrolled up to read history, streaming must NOT auto-scroll.
    /// The smart threshold in chatScroll.scrollToBottom preserves reading position.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Mobile")]
    [Trait("Area", "Scroll")]
    public async Task MobilePortal_UserScrolledUp_StreamingDoesNotAutoScroll()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, page, mobilePage) = await TryLaunchMobileAsync(playwright);
        Skip.If(browser is null, "Browser not available");
        await using var _ = browser!;

        await mobilePage!.NavigateAsync(_fx.GatewayBaseUrl);
        await mobilePage.WaitForReadyAsync();

        // Scroll to top to simulate reading history
        await mobilePage.ScrollToTopAsync();
        await page!.WaitForTimeoutAsync(100);

        // Send — triggers streaming
        await mobilePage.SendMessageAsync("SLOW_STREAM");
        await page.WaitForTimeoutAsync(400);

        // Scroll position should not have jumped to bottom
        var scrollTop = await mobilePage.GetScrollTopAsync();
        var scrollHeight = await page.EvaluateAsync<double>(
            "() => document.querySelector('.message-stream')?.scrollHeight ?? 0");
        var clientHeight = await page.EvaluateAsync<double>(
            "() => document.querySelector('.message-stream')?.clientHeight ?? 0");

        var isAtBottom = scrollHeight - scrollTop - clientHeight < 100;
        Assert.False(isAtBottom,
            "When user has scrolled up to read history, streaming must NOT force-scroll to bottom. " +
            "chatScroll.scrollToBottom threshold (200px) should preserve reading position.");

        // Cleanup — wait for stream to end
        try { await mobilePage.WaitForStreamingCompleteAsync(20_000); } catch { }
    }

    /// <summary>
    /// Mobile top bar controls (agent select, conv select, overflow button)
    /// all fit within the mobile viewport without causing horizontal scrolling.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Mobile")]
    [Trait("Area", "Layout")]
    public async Task MobilePortal_TopBar_AllControlsFitWithoutHorizontalScroll()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, page, mobilePage) = await TryLaunchMobileAsync(playwright);
        Skip.If(browser is null, "Browser not available");
        await using var _ = browser!;

        await mobilePage!.NavigateAsync(_fx.GatewayBaseUrl);
        await mobilePage.WaitForReadyAsync();

        Assert.True(await mobilePage.AgentSelect.IsVisibleAsync(), "Agent select must be visible");
        Assert.True(await mobilePage.ConvSelect.IsVisibleAsync(), "Conv select must be visible");
        Assert.True(await mobilePage.OverflowButton.IsVisibleAsync(), "Overflow button must be visible");

        var hasHorizontalScroll = await page!.EvaluateAsync<bool>(
            "() => document.body.scrollWidth > document.body.clientWidth");
        Assert.False(hasHorizontalScroll,
            "Mobile top bar must not cause horizontal scrolling on a 390px viewport");
    }

    /// <summary>
    /// Overflow menu opens on tap and exposes the New Session action.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Mobile")]
    [Trait("Area", "Navigation")]
    public async Task MobilePortal_OverflowMenu_ShowsNewSessionAction()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, page, mobilePage) = await TryLaunchMobileAsync(playwright);
        Skip.If(browser is null, "Browser not available");
        await using var _ = browser!;

        await mobilePage!.NavigateAsync(_fx.GatewayBaseUrl);
        await mobilePage.WaitForReadyAsync();

        await mobilePage.OverflowButton.TapAsync();

        await mobilePage.OverflowDropdown.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 5_000
        });

        var newSessionBtn = mobilePage.OverflowDropdown.Locator(".menu-action");
        Assert.True(await newSessionBtn.IsVisibleAsync(), "New session button must be in overflow menu");

        var text = await newSessionBtn.InnerTextAsync();
        Assert.Contains("session", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Send button is disabled when input is empty, enabled when text is present.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Mobile")]
    [Trait("Area", "Input")]
    public async Task MobilePortal_SendButton_StateFollowsInputContent()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, page, mobilePage) = await TryLaunchMobileAsync(playwright);
        Skip.If(browser is null, "Browser not available");
        await using var _ = browser!;

        await mobilePage!.NavigateAsync(_fx.GatewayBaseUrl);
        await mobilePage.WaitForReadyAsync();

        var textarea = mobilePage.InputTextarea;
        var sendBtn = mobilePage.SendButton;

        await textarea.FillAsync("");
        Assert.True(await sendBtn.IsDisabledAsync(), "Send must be disabled when input is empty");

        await textarea.FillAsync("hello mobile");
        Assert.True(await sendBtn.IsEnabledAsync(), "Send must be enabled when input has text");
    }
}
