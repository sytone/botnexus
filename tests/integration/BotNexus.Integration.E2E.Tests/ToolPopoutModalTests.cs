using Microsoft.Playwright;
using BotNexus.Integration.E2E.Tests.PageObjects;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// #1948 — Playwright coverage for the tool Arguments/Result pop-out modal.
///
/// Asserts:
///   1. A tool payload containing \n, \t and a \uXXXX escape, when popped out,
///      renders REAL newlines and the ACTUAL unicode glyph (assert on rendered
///      text geometry + content, not just presence of the escape string).
///   2. The modal opens, is focus-trapped, and closes via Esc / close button / backdrop.
///   3. The Copy button still yields the RAW (un-decoded) payload.
///   4. REGRESSION GUARD — opening/closing the tool modal does not break sibling
///      layout: the portal settings overlay (#1944 shared-CSS class) still opens
///      fixed-position and within the viewport, and the chat input stays interactable.
///
/// Mirrors PortalSettingsPanelTests.cs / ChatHeaderOverflowMenuTests.cs.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class ToolPopoutModalTests
{
    private readonly NewUserExperienceFixture _fx;

    public ToolPopoutModalTests(NewUserExperienceFixture fx) => _fx = fx;

    private async Task<(IPage page, PortalPage portal, ChatPanelPage chat)> SetupToolMessageAsync(IBrowser browser)
    {
        var (page, portal, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await chat.StartFreshSessionAsync();
        await chat.SendMessageAsync("TOOL_UNICODE_SEQUENCE");
        await chat.WaitForStreamingCompleteAsync(TimeSpan.FromSeconds(30));

        // Tool message should appear, then expand it to reveal the sections.
        var toolMessage = page.Locator(".message.tool").First;
        await toolMessage.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = 15_000,
        });

        await chat.Root.Locator(".tool-header").First.ClickAsync();

        return (page, portal, chat);
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "ToolPopout")]
    public async Task Popout_DecodesEscapes_RendersRealNewlinesAndGlyph()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;

        var (page, _, chat) = await SetupToolMessageAsync(browser);

        // Pop out the Arguments section (carries the escaped note payload).
        var popout = chat.Root.Locator("[data-testid='tool-popout-args']").First;
        await popout.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 10_000 });
        // Force click — the button is opacity:0 until hover, but it is interactable.
        await popout.ClickAsync(new LocatorClickOptions { Force = true });

        var content = page.Locator("[data-testid='tool-modal-content']");
        await content.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        var text = await content.InnerTextAsync();

        // The actual glyph must be present, and the raw \uXXXX escape must be gone.
        Assert.Contains("\u2705", text);
        Assert.DoesNotContain("\\u2705", text);

        // Real newline present in the rendered text (not the literal two-char "\n").
        Assert.Contains("\n", text);
        Assert.DoesNotContain("\\n", text);

        // Geometry: a <pre> with real newlines renders taller than a single line.
        var box = await content.BoundingBoxAsync();
        Assert.NotNull(box);
        var lineHeight = await content.EvaluateAsync<double>(
            "el => parseFloat(getComputedStyle(el).lineHeight) || parseFloat(getComputedStyle(el).fontSize) * 1.4");
        Assert.True(box!.Height > lineHeight * 1.5,
            $"Modal content height ({box.Height}) should span multiple lines (line-height {lineHeight}) — " +
            "escaped newlines must render as real line breaks.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "ToolPopout")]
    public async Task Modal_Opens_IsFocusTrapped_ClosesViaEscCloseAndBackdrop()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;

        var (page, _, chat) = await SetupToolMessageAsync(browser);

        var overlay = page.Locator("[data-testid='tool-modal-overlay']");
        var popout = chat.Root.Locator("[data-testid='tool-popout-result']").First;

        // --- 1. Close via Esc ------------------------------------------------
        await popout.ClickAsync(new LocatorClickOptions { Force = true });
        await overlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        // Focus trap: focus should be inside the dialog after open.
        var focusInside = await page.EvaluateAsync<bool>(
            "() => { var d = document.querySelector(\"[data-testid='tool-modal']\"); return d ? d.contains(document.activeElement) || d === document.activeElement : false; }");
        Assert.True(focusInside, "Focus should be trapped inside the tool modal after it opens.");

        await page.Keyboard.PressAsync("Escape");
        await overlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 5_000 });

        // --- 2. Close via close button --------------------------------------
        await popout.ClickAsync(new LocatorClickOptions { Force = true });
        await overlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        await page.Locator("[data-testid='tool-modal-close']").ClickAsync();
        await overlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 5_000 });

        // --- 3. Close via backdrop click ------------------------------------
        await popout.ClickAsync(new LocatorClickOptions { Force = true });
        await overlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        // Click near the top-left corner of the overlay (outside the centered dialog).
        await page.Mouse.ClickAsync(10, 10);
        await overlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 8_000 });
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "ToolPopout")]
    public async Task CopyButton_YieldsRawPayload_NotDecoded()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;

        var context = await browser!.NewContextAsync();
        await context.GrantPermissionsAsync(new[] { "clipboard-read", "clipboard-write" });
        var page = await context.NewPageAsync();
        var portal = new PortalPage(page);
        await portal.GotoAgentChatAsync(_fx.GatewayBaseUrl, _fx.AgentIds[0]);
        var chat = new ChatPanelPage(page, _fx.AgentIds[0]);

        await chat.StartFreshSessionAsync();
        await chat.SendMessageAsync("TOOL_UNICODE_SEQUENCE");
        await chat.WaitForStreamingCompleteAsync(TimeSpan.FromSeconds(30));

        var toolMessage = page.Locator(".message.tool").First;
        await toolMessage.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 15_000 });
        await chat.Root.Locator(".tool-header").First.ClickAsync();

        // Click the Arguments copy button (first .tool-copy-btn in the expanded tool).
        var copyBtn = chat.Root.Locator(".tool-copy-btn").First;
        await copyBtn.ClickAsync(new LocatorClickOptions { Force = true });

        // The raw payload must still contain the literal escape sequences (lossless copy).
        var clip = await page.EvaluateAsync<string>("async () => await navigator.clipboard.readText()");
        Assert.Contains("\\n", clip);
        Assert.Contains("\\u2705", clip);
        // And must NOT contain the decoded glyph.
        Assert.DoesNotContain("\u2705", clip);

        await context.CloseAsync();
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "ToolPopout")]
    public async Task ModalOpenClose_DoesNotBreakSiblingLayout_SettingsModalGuard()
    {
        // Regression guard for the #1944 class of bug: a stray/unclosed CSS rule in
        // the tool-modal block must not leak into sibling modals. After opening and
        // closing the tool modal, the shared portal-settings overlay must still open
        // fixed-position and within the viewport, and the chat input stays interactable.
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;

        var (page, portal, chat) = await SetupToolMessageAsync(browser);

        var overlay = page.Locator("[data-testid='tool-modal-overlay']");
        var popout = chat.Root.Locator("[data-testid='tool-popout-args']").First;

        // Open + close the tool modal.
        await popout.ClickAsync(new LocatorClickOptions { Force = true });
        await overlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        await page.Locator("[data-testid='tool-modal-close']").ClickAsync();
        await overlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 5_000 });

        // --- Sibling guard 1: portal settings overlay still healthy ----------
        await portal.BannerSettingsBtn.ClickAsync();
        var settingsOverlay = page.Locator(".portal-settings-overlay");
        await settingsOverlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        var position = await settingsOverlay.EvaluateAsync<string>("el => getComputedStyle(el).position");
        Assert.Equal("fixed", position);

        var viewport = page.ViewportSize!;
        var panelBox = await page.Locator(".portal-settings-panel").BoundingBoxAsync();
        Assert.NotNull(panelBox);
        Assert.True(panelBox!.Y >= 0 && panelBox.Y < viewport.Height,
            $"Settings panel top ({panelBox.Y}) is outside the viewport (height {viewport.Height}) — " +
            "tool-modal CSS may have leaked into sibling modals (see #1944).");
        Assert.True(panelBox.Width > 0 && panelBox.Height > 0, "Settings panel has zero size after tool modal cycle.");

        // Close settings.
        await page.Locator(".portal-settings-close").ClickAsync();
        await settingsOverlay.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 5_000 });

        // --- Sibling guard 2: chat input remains interactable ----------------
        await chat.ChatInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        await chat.ChatInput.FillAsync("still-interactable");
        Assert.Equal("still-interactable", await chat.ChatInput.InputValueAsync());
    }
}
