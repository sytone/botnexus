using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Regression tests for issue #793 — mobile chat panel auto-scroll.
///
/// Root causes identified:
///   1. chatScroll.js was NOT referenced in the mobile index.html — every
///      JS.InvokeVoidAsync("chatScroll.*") call in Chat.razor silently threw
///      and was swallowed by the empty catch{} blocks, so no scroll ever happened.
///   2. OnAfterRenderAsync was not overridden in Chat.razor — history loaded on
///      mount/navigation never triggered a scroll to the bottom.
///
/// Tests 1 and 2 are static (no gateway needed). Tests 3-5 require a live gateway.
/// The bug is proven by tests 1+2 failing before the fix, passing after.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class MobileScrollTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _out;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private IBrowserContext _ctx = null!;
    private IPage _page = null!;

    // Mobile viewport matching chatScroll.isMobileView() breakpoint (<=768px)
    private static readonly BrowserNewContextOptions MobileContextOptions = new()
    {
        ViewportSize = new ViewportSize { Width = 390, Height = 844 },
        UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Safari/604.1",
        IsMobile = true,
        HasTouch = true,
    };

    public MobileScrollTests(NewUserExperienceFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    public async Task InitializeAsync()
    {
        await PlaywrightBootstrap.EnsureBrowserInstalledAsync();
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
        _ctx = await _browser.NewContextAsync(MobileContextOptions);
        _page = await _ctx.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        await _page.CloseAsync();
        await _ctx.CloseAsync();
        await _browser.CloseAsync();
        _playwright.Dispose();
    }

    // -------------------------------------------------------------------------
    // Test 1 — STATIC: chatScroll.js must be referenced in mobile index.html.
    // This test reads the actual source file; no gateway required.
    // Proves bug #1: the script tag was missing so all scroll JS interop failed.
    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Mobile")]
    [Trait("Issue", "793")]
    public void MobileIndexHtml_MustReference_ChatScrollJs()
    {
        // Find the mobile index.html relative to the repo root
        var repoRoot = RepoLocator.FindRepoRoot();
        var indexHtml = Path.Combine(
            repoRoot,
            "src", "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile",
            "wwwroot", "index.html");

        Skip.If(!File.Exists(indexHtml), $"Mobile index.html not found at {indexHtml}");

        var html = File.ReadAllText(indexHtml);
        _out.WriteLine($"index.html path: {indexHtml}");
        _out.WriteLine($"index.html content:\n{html}");

        Assert.True(html.Contains("chatScroll.js"),
            "chatScroll.js MUST be referenced in the mobile index.html. " +
            "Without this script tag every JS.InvokeVoidAsync(\"chatScroll.*\") call in " +
            "Chat.razor silently throws (swallowed by catch{}) and NO scroll ever fires. " +
            "Fix: add <script src=\"js/chatScroll.js\"></script> before blazor.webassembly.js.");
    }

    // -------------------------------------------------------------------------
    // Test 2 — STATIC: Chat.razor must override OnAfterRenderAsync for history scroll.
    // Reads the source file directly; no gateway required.
    // Proves bug #2: initial history load never triggered a scroll.
    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Mobile")]
    [Trait("Issue", "793")]
    public void MobileChatRazor_MustOverride_OnAfterRenderAsync()
    {
        var repoRoot = RepoLocator.FindRepoRoot();
        var chatRazor = Path.Combine(
            repoRoot,
            "src", "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile",
            "Pages", "Chat.razor");

        Skip.If(!File.Exists(chatRazor), $"Mobile Chat.razor not found at {chatRazor}");

        var source = File.ReadAllText(chatRazor);

        Assert.True(source.Contains("OnAfterRenderAsync"),
            "Chat.razor MUST override OnAfterRenderAsync. " +
            "Without it, when history is loaded on page mount or navigation the view " +
            "stays at scroll position 0 instead of scrolling to the most recent message. " +
            "Fix: override OnAfterRenderAsync and call ForceScrollToBottomAsync() when firstRender==true " +
            "and messages are present.");
    }

    // -------------------------------------------------------------------------
    // Test 3 — LIVE: window.chatScroll must be defined in the running mobile page.
    // Requires gateway. Proves the JS actually loads at runtime.
    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Mobile")]
    [Trait("Issue", "793")]
    public async Task MobilePage_ChatScrollNamespace_IsDefinedAfterLoad()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var mobileUrl = $"{_fx.GatewayBaseUrl}/mobile/";

        var response = await _page.GotoAsync(mobileUrl, new() { Timeout = 30_000 });
        Assert.True(response?.Ok == true, $"Mobile portal failed to load. Status: {response?.Status}");

        // Wait for Blazor WASM to boot
        await _page.WaitForSelectorAsync(
            "#app .mobile-app, #app .portal-loading, #app .portal-load-error",
            new() { Timeout = 30_000 });

        var defined = await _page.EvaluateAsync<bool>(
            "typeof window.chatScroll !== 'undefined' && typeof window.chatScroll.scrollToBottom === 'function'");

        Assert.True(defined,
            "window.chatScroll.scrollToBottom must be defined after page load. " +
            "chatScroll.js is not loaded in the mobile index.html.");
    }

    // -------------------------------------------------------------------------
    // Test 4 — LIVE: After history loads on mount, view must be scrolled to bottom.
    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Mobile")]
    [Trait("Issue", "793")]
    public async Task MobileChat_OnHistoryLoad_ScrollsToBottom()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        // Navigate to first agent which will have provisioned conversations
        var agentId = _fx.AgentIds[0];
        var mobileUrl = $"{_fx.GatewayBaseUrl}/mobile/chat/{Uri.EscapeDataString(agentId)}";

        await _page.GotoAsync(mobileUrl, new() { Timeout = 30_000 });
        await _page.WaitForSelectorAsync(".mobile-app", new() { Timeout = 30_000 });

        // Wait for at least one message to render
        var msgLocator = _page.Locator(".message-stream .message, .message-stream .tool-pill");
        await msgLocator.First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 20_000 });

        // Allow OnAfterRenderAsync + requestAnimationFrame + 50ms backstop to complete
        await _page.WaitForTimeoutAsync(500);

        var scrollInfo = await GetScrollInfoAsync();
        _out.WriteLine($"History load scroll: top={scrollInfo.ScrollTop:F0} height={scrollInfo.ScrollHeight:F0} client={scrollInfo.ClientHeight:F0} atBottom={scrollInfo.AtBottom}");

        Assert.True(scrollInfo.AtBottom,
            $"Message stream must be scrolled to bottom after history loads on mount. " +
            $"scrollTop={scrollInfo.ScrollTop:F0}, scrollHeight={scrollInfo.ScrollHeight:F0}, clientHeight={scrollInfo.ClientHeight:F0}. " +
            "Fix: override OnAfterRenderAsync in Chat.razor and call ForceScrollToBottomAsync() on firstRender.");
    }

    // -------------------------------------------------------------------------
    // Test 5 — LIVE: After sending a message, view scrolls to bottom.
    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Mobile")]
    [Trait("Issue", "793")]
    public async Task MobileChat_AfterSendMessage_ScrollsToBottom()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        var agentId = _fx.AgentIds[0];
        var mobileUrl = $"{_fx.GatewayBaseUrl}/mobile/chat/{Uri.EscapeDataString(agentId)}";

        await _page.GotoAsync(mobileUrl, new() { Timeout = 30_000 });
        await _page.WaitForSelectorAsync(".mobile-app", new() { Timeout = 30_000 });
        await _page.WaitForSelectorAsync(".input-textarea", new() { Timeout = 20_000 });

        var beforeCount = await _page.Locator(".message-stream .message").CountAsync();

        await _page.Locator(".input-textarea").FillAsync("ping");
        await _page.Locator(".send-btn").ClickAsync();

        // Wait for user message to appear
        await _page.WaitForFunctionAsync(
            $"document.querySelectorAll('.message-stream .message').length > {beforeCount}",
            null, new() { Timeout = 15_000 });

        // Allow requestAnimationFrame + 50ms backstop to complete
        await _page.WaitForTimeoutAsync(400);

        var scrollInfo = await GetScrollInfoAsync();
        _out.WriteLine($"After send scroll: top={scrollInfo.ScrollTop:F0} height={scrollInfo.ScrollHeight:F0} atBottom={scrollInfo.AtBottom}");

        Assert.True(scrollInfo.AtBottom,
            "Message stream must scroll to bottom after sending a message. " +
            "chatScroll.js must be loaded and ConditionalScrollAsync must be wired to Store.OnChanged.");
    }

    // -------------------------------------------------------------------------
    // Test 6 — LIVE: When user has scrolled up, a new message must NOT auto-scroll.
    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Mobile")]
    [Trait("Issue", "793")]
    public async Task MobileChat_WhenUserScrolledUp_IncomingMessage_PreservesScrollPosition()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        var agentId = _fx.AgentIds[0];
        var mobileUrl = $"{_fx.GatewayBaseUrl}/mobile/chat/{Uri.EscapeDataString(agentId)}";

        await _page.GotoAsync(mobileUrl, new() { Timeout = 30_000 });
        await _page.WaitForSelectorAsync(".mobile-app", new() { Timeout = 30_000 });
        await _page.WaitForSelectorAsync(".message-stream .message", new() { Timeout = 20_000 });

        // Manually scroll to top to simulate user reading history
        await _page.EvaluateAsync("() => { const el = document.querySelector('.message-stream'); if (el) el.scrollTop = 0; }");
        await _page.WaitForTimeoutAsync(150);

        var beforeCount = await _page.Locator(".message-stream .message").CountAsync();

        // Send a message — this causes a new message to arrive
        await _page.Locator(".input-textarea").FillAsync("preserve-scroll-test");
        await _page.Locator(".send-btn").ClickAsync();

        await _page.WaitForFunctionAsync(
            $"document.querySelectorAll('.message-stream .message').length > {beforeCount}",
            null, new() { Timeout = 15_000 });
        await _page.WaitForTimeoutAsync(400);

        var scrollInfo = await GetScrollInfoAsync();
        _out.WriteLine($"Preserve scroll: top={scrollInfo.ScrollTop:F0} height={scrollInfo.ScrollHeight:F0} client={scrollInfo.ClientHeight:F0} atBottom={scrollInfo.AtBottom}");

        // The view should NOT have jumped to the bottom (threshold is 100px per chatScroll logic)
        Assert.False(scrollInfo.AtBottom,
            "When the user has manually scrolled up, a new incoming message must NOT force-scroll " +
            "back to the bottom. chatScroll.scrollToBottom threshold logic must preserve user position.");
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private async Task<ScrollInfo> GetScrollInfoAsync() =>
        await _page.EvaluateAsync<ScrollInfo>(@"() => {
            const el = document.querySelector('.message-stream');
            if (!el) return { scrollTop: 0, scrollHeight: 0, clientHeight: 0, atBottom: false };
            const atBottom = Math.abs(el.scrollHeight - el.scrollTop - el.clientHeight) < 50;
            return { scrollTop: el.scrollTop, scrollHeight: el.scrollHeight, clientHeight: el.clientHeight, atBottom };
        }");

    private sealed class ScrollInfo
    {
        public double ScrollTop { get; set; }
        public double ScrollHeight { get; set; }
        public double ClientHeight { get; set; }
        public bool AtBottom { get; set; }
    }
}
