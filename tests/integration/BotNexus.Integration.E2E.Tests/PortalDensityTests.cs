using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Regression gate for the #2441 portal density overhaul. Screenshots are useful for review but
/// are not a regression gate, so this suite asserts the invariants numerically:
///
///  * the top bar, tab strip and conversation title stay within a bounded height regardless of
///    what text is poured into them (300-char values, empty values, multi-codepoint ZWJ emoji,
///    combining marks, and embedded newline / tab / carriage-return control characters);
///  * nothing clips into or overlaps a neighbouring control;
///  * no control character survives into rendered chrome text;
///  * the density token set is present and switches with the data-density attribute.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class PortalDensityTests : IAsyncLifetime
{
    private readonly NewUserExperienceFixture _fx;
    private readonly ITestOutputHelper _out;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public PortalDensityTests(NewUserExperienceFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    public async Task InitializeAsync()
    {
        await PlaywrightBootstrap.EnsureBrowserInstalledAsync();
        _playwright = await Playwright.CreateAsync();
        _browser = await PlaywrightBootstrap.LaunchChromiumAsync(_playwright);
    }

    public async Task DisposeAsync()
    {
        await _browser.CloseAsync();
        _playwright.Dispose();
    }

    // ~300 chars, no spaces, so word-wrapping cannot rescue a broken single-line rule.
    private static readonly string LongValue = new('W', 300);

    private const string ZwjFamily = "\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466";
    private const string Combining = "e\u0301\u0327";
    private const string ControlChars = "Line\nOne\tTabbed\rReturned";

    private static readonly string[] AdversarialValues =
    [
        LongValue,
        "",
        " ",
        "x",
        ZwjFamily,
        Combining,
        ControlChars,
        LongValue + "\n" + LongValue,
        ZwjFamily + "\t" + LongValue
    ];

    /// <summary>
    /// Overwrites the store-visible chrome text via the DOM so a single browser session can be
    /// driven through every adversarial value without needing a distinct server-side agent for
    /// each. The layout invariants under test are purely presentational.
    /// </summary>
    private static async Task SetChromeTextAsync(IPage page, string selector, string value)
    {
        await page.EvaluateAsync(
            "([sel, v]) => { const el = document.querySelector(sel); if (el) { el.textContent = v; } }",
            new object[] { selector, value });
        // Give layout a beat to reflow before measuring.
        await page.WaitForTimeoutAsync(60);
    }

    /// <summary>Resolves the worktree tmp/screenshots-2441 directory, creating it if needed.</summary>
    private static string ScreenshotDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "tests")))
            d = d.Parent;
        var root = d?.FullName ?? Path.GetTempPath();
        var dir = Path.Combine(root, "tmp", "screenshots-2441");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<double> HeightOfAsync(IPage page, string selector)
    {
        var box = await page.Locator(selector).First.BoundingBoxAsync();
        return box?.Height ?? 0;
    }

    private static async Task<bool> IsClippedAsync(IPage page, string selector) =>
        await page.EvaluateAsync<bool>(
            "sel => { const el = document.querySelector(sel); if (!el) return false; " +
            "return el.scrollHeight > el.clientHeight + 1; }",
            selector);

    // -------------------------------------------------------------------------
    [SkippableTheory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 800)]
    [InlineData(390, 844)]
    [Trait("Category", "PortalDensity")]
    public async Task TopBar_DoesNotGrow_ForAnyAdversarialIdentityText(int width, int height)
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);
        await page.SetViewportSizeAsync(width, height);

        var bar = page.Locator(".banner-header").First;
        await bar.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        var baseline = await HeightOfAsync(page, ".banner-header");
        Assert.True(baseline > 0, "Top bar has no measurable height.");
        _out.WriteLine($"{width}x{height} baseline top bar height={baseline}");

        foreach (var value in AdversarialValues)
        {
            await SetChromeTextAsync(page, "[data-testid='agent-identity-name']", value);
            await SetChromeTextAsync(page, ".banner-agent-identity .agent-panel-description", value);

            var now = await HeightOfAsync(page, ".banner-header");
            _out.WriteLine($"  value len={value.Length} -> height={now}");

            // A 1px tolerance covers sub-pixel rounding; anything more means the row grew.
            Assert.True(now <= baseline + 1,
                $"Top bar grew from {baseline} to {now} for a {value.Length}-char value at {width}x{height}.");
        }
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "PortalDensity")]
    public async Task TopBarIdentity_TruncatesInsteadOfOverflowing()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);
        await page.SetViewportSizeAsync(1280, 800);

        var name = page.Locator("[data-testid='agent-identity-name']").First;
        await name.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        await SetChromeTextAsync(page, "[data-testid='agent-identity-name']", LongValue);

        var nameBox = await name.BoundingBoxAsync();
        var barBox = await page.Locator(".banner-header").First.BoundingBoxAsync();
        Assert.NotNull(nameBox);
        Assert.NotNull(barBox);

        // The name must stay inside the bar horizontally: it truncates, it does not overflow.
        Assert.True(nameBox!.X + nameBox.Width <= barBox!.X + barBox.Width + 1,
            $"Identity name (right={nameBox.X + nameBox.Width}) overflows the top bar " +
            $"(right={barBox.X + barBox.Width}).");

        // And it must be a single line: one line-height, not several.
        Assert.True(nameBox.Height <= barBox.Height + 1,
            $"Identity name wrapped to {nameBox.Height}px inside a {barBox.Height}px bar.");

        Assert.False(await IsClippedAsync(page, ".banner-header"),
            "Top bar content is clipped vertically - something is taller than the bar.");
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "PortalDensity")]
    public async Task TopBarIdentity_DoesNotOverlapSettingsButton()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);
        await page.SetViewportSizeAsync(1280, 800);

        await page.Locator("[data-testid='agent-identity']").First
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        await SetChromeTextAsync(page, "[data-testid='agent-identity-name']", LongValue);

        var identityBox = await page.Locator("[data-testid='agent-identity']").First.BoundingBoxAsync();
        var settingsBox = await page.Locator("[data-testid='banner-settings-btn']").First.BoundingBoxAsync();
        Assert.NotNull(identityBox);
        Assert.NotNull(settingsBox);

        Assert.True(identityBox!.X + identityBox.Width <= settingsBox!.X + 1,
            $"Identity (right={identityBox.X + identityBox.Width}) overlaps the settings button " +
            $"(left={settingsBox.X}).");
        Assert.True(settingsBox.Width > 0, "Settings button collapsed to zero width.");
    }

    // -------------------------------------------------------------------------
    // Control characters must never reach the DOM as literal whitespace: they are normalised
    // server-side (PortalText.SingleLine) so no CSS rule has to save the layout.
    [SkippableFact]
    [Trait("Category", "PortalDensity")]
    public async Task RenderedChromeText_ContainsNoControlCharacters()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);
        await page.SetViewportSizeAsync(1280, 800);

        await page.Locator("[data-testid='agent-identity-name']").First
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        foreach (var selector in new[]
                 {
                     "[data-testid='agent-identity-name']",
                     ".conversation-title",
                     ".conversation-list-item-title"
                 })
        {
            var text = await page.EvaluateAsync<string?>(
                "sel => document.querySelector(sel)?.textContent ?? null", selector);
            if (text is null)
                continue;

            _out.WriteLine($"{selector} => '{text}'");
            Assert.DoesNotContain('\n', text);
            Assert.DoesNotContain('\r', text);
            Assert.DoesNotContain('\t', text);
        }
    }

    // -------------------------------------------------------------------------
    [SkippableTheory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 800)]
    [InlineData(390, 844)]
    [Trait("Category", "PortalDensity")]
    public async Task ConversationTitleRow_DoesNotGrow_ForAdversarialTitles(int width, int height)
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);
        await page.SetViewportSizeAsync(width, height);

        var header = page.Locator(".chat-header").First;
        await header.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        var baseline = await HeightOfAsync(page, ".chat-header");
        Assert.True(baseline > 0, "Chat header has no measurable height.");

        foreach (var value in AdversarialValues)
        {
            await SetChromeTextAsync(page, ".conversation-title", value);
            var now = await HeightOfAsync(page, ".chat-header");
            Assert.True(now <= baseline + 1,
                $"Conversation title row grew from {baseline} to {now} for a {value.Length}-char value.");
        }
    }

    // -------------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "PortalDensity")]
    public async Task TabStrip_StaysSingleRow_AndBelowComfortableHeight()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);
        await page.SetViewportSizeAsync(1280, 800);

        var strip = page.Locator(".agent-panel-tab-strip").First;
        await strip.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        var stripBox = await strip.BoundingBoxAsync();
        Assert.NotNull(stripBox);
        _out.WriteLine($"tab strip height={stripBox!.Height}");

        // Compact preset: the strip used to be 40-42px. It must now be materially slimmer.
        Assert.True(stripBox.Height <= 36,
            $"Compact tab strip should be <= 36px, measured {stripBox.Height}px.");

        // Every tab must sit on the same row as the strip - no wrapping. Scope to the VISIBLE
        // strip: the portal keeps one agent panel per agent in the DOM and hides the inactive
        // ones, whose tabs have no bounding box at all.
        var tabs = strip.Locator(".agent-panel-tab");
        var count = await tabs.CountAsync();
        Assert.True(count > 0, "No tabs rendered.");
        var measured = 0;
        for (var i = 0; i < count; i++)
        {
            var tabBox = await tabs.Nth(i).BoundingBoxAsync();
            if (tabBox is null)
                continue;
            measured++;
            Assert.True(tabBox.Y >= stripBox.Y - 1 && tabBox.Y + tabBox.Height <= stripBox.Y + stripBox.Height + 1,
                $"Tab {i} (y={tabBox.Y} h={tabBox.Height}) escapes the tab strip " +
                $"(y={stripBox.Y} h={stripBox.Height}) - the strip wrapped.");
        }

        Assert.True(measured > 0, "No visible tabs could be measured in the active tab strip.");
    }

    // -------------------------------------------------------------------------
    // The density token set must actually drive layout: switching data-density to comfortable
    // has to make the chrome taller, otherwise the tokens are decorative.
    [SkippableFact]
    [Trait("Category", "PortalDensity")]
    public async Task ComfortableDensity_ProducesTallerChromeThanCompact()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);
        await page.SetViewportSizeAsync(1280, 800);

        await page.Locator(".banner-header").First
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        var shell = page.Locator(".app-shell").First;
        Assert.Equal("compact", await shell.GetAttributeAsync("data-density"));

        var compactBar = await HeightOfAsync(page, ".banner-header");
        var compactStrip = await HeightOfAsync(page, ".agent-panel-tab-strip");

        await page.EvaluateAsync(
            "() => document.querySelector('.app-shell')?.setAttribute('data-density', 'comfortable')");
        await page.WaitForTimeoutAsync(120);

        var comfyBar = await HeightOfAsync(page, ".banner-header");
        var comfyStrip = await HeightOfAsync(page, ".agent-panel-tab-strip");

        _out.WriteLine($"bar compact={compactBar} comfortable={comfyBar}");
        _out.WriteLine($"strip compact={compactStrip} comfortable={comfyStrip}");

        Assert.True(comfyBar > compactBar,
            $"Comfortable top bar ({comfyBar}) should be taller than compact ({compactBar}).");
        Assert.True(comfyStrip > compactStrip,
            $"Comfortable tab strip ({comfyStrip}) should be taller than compact ({compactStrip}).");
    }

    // -------------------------------------------------------------------------
    // Screenshot capture. Not itself a gate - the assertions above are - but the PNGs are the
    // artefact a human reviews, so producing them is part of the suite.
    [SkippableTheory]
    [InlineData(1920, 1080, "desktop-1920")]
    [InlineData(1280, 800, "desktop-1280")]
    [InlineData(390, 844, "mobile-390")]
    [Trait("Category", "PortalDensity")]
    public async Task CaptureDensityScreenshots(int width, int height, string label)
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            _browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);
        await page.SetViewportSizeAsync(width, height);
        await page.Locator(".banner-header").First
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        // On mobile the sidebar opens as an overlay that covers the top bar, which is exactly
        // what these shots need to show, so dismiss it before capturing.
        // The overlay is a full-screen click-catcher sitting above the burger, so clicking either
        // is unreliable (and a forced burger click lands on whatever nav item is underneath).
        // Collapse the drawer directly in the DOM - this shot is about the top bar, not the nav.
        await page.EvaluateAsync(
            "() => { document.querySelector('.sidebar-overlay')?.remove(); " +
            "const s = document.querySelector('.main-sidebar'); " +
            "if (s) { s.classList.remove('sidebar-open'); s.classList.add('sidebar-closed'); } }");
        await page.WaitForTimeoutAsync(300);

        var dir = ScreenshotDir();

        var compactPath = Path.Combine(dir, $"after-compact-{label}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = compactPath });

        await page.EvaluateAsync(
            "() => document.querySelector('.app-shell')?.setAttribute('data-density', 'comfortable')");
        await page.WaitForTimeoutAsync(150);
        var comfyPath = Path.Combine(dir, $"after-comfortable-{label}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = comfyPath });

        await page.EvaluateAsync(
            "() => document.querySelector('.app-shell')?.setAttribute('data-density', 'compact')");
        await page.WaitForTimeoutAsync(120);

        var cases = new (string Name, string Value)[]
        {
            ("long300", LongValue),
            ("onechar", "x"),
            ("empty", ""),
            ("zwj", ZwjFamily + Combining),
            ("controlchars", ControlChars),
            ("zwjlong", ZwjFamily + "\t" + LongValue)
        };

        foreach (var (caseName, value) in cases)
        {
            await SetChromeTextAsync(page, "[data-testid='agent-identity-name']", value);
            await SetChromeTextAsync(page, ".banner-agent-identity .agent-panel-description", value);
            await SetChromeTextAsync(page, ".conversation-title", value);
            await page.EvaluateAsync(
                "v => document.querySelectorAll('.conversation-list-item-title').forEach(e => { e.textContent = v; })",
                value);
            await page.WaitForTimeoutAsync(150);

            var shot = Path.Combine(dir, $"adversarial-{caseName}-{label}.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = shot });
            _out.WriteLine($"screenshot: {shot}");

            var nav = page.Locator(".main-sidebar").First;
            if (await nav.IsVisibleAsync())
            {
                var navShot = Path.Combine(dir, $"adversarial-{caseName}-{label}-leftnav.png");
                await nav.ScreenshotAsync(new LocatorScreenshotOptions { Path = navShot });
                _out.WriteLine($"screenshot: {navShot}");
            }
        }

        _out.WriteLine($"screenshot: {compactPath}");
        _out.WriteLine($"screenshot: {comfyPath}");
        Assert.True(File.Exists(compactPath));
        Assert.True(File.Exists(comfyPath));
    }
}
