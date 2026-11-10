using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BotNexus.Integration.E2E.Tests.PageObjects;
using Microsoft.Playwright;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Real candidate-portal evidence for #5095. These diagnostics require the remote runner:
/// fixture, browser, API seeding and artifact failures fail the test rather than become skips.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
[Trait("Category", "FocusedSidebar")]
public sealed class FocusedSidebarBrowserTests
{
    private readonly NewUserExperienceFixture _fixture;
    private readonly ITestOutputHelper _output;

    public FocusedSidebarBrowserTests(NewUserExperienceFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData("light", 1440, 900)]
    [InlineData("dark", 1440, 900)]
    [InlineData("light", 600, 844)]
    [InlineData("dark", 600, 844)]
    public async Task FocusedSidebar_PopulatedChatAndAgents_ScrollIndependentlyWithFixedPicker(
        string theme, int width, int height)
    {
        AssertFixture();
        var invocation = new InvocationContext(
            Environment.GetEnvironmentVariable("RUN_ID")
                ?? throw new InvalidOperationException("Browser invocation evidence requires RUN_ID."),
            Guid.NewGuid().ToString("N"));
        var artifacts = ArtifactDirectory(invocation);
        var captures = new List<string>();
        using var http = new HttpClient { BaseAddress = new Uri(_fixture.GatewayBaseUrl) };
        var data = await SeedAsync(http, 36);
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await PlaywrightBootstrap.LaunchChromiumAsync(playwright);
            await using var context = await browser.NewContextAsync(new()
            {
                ViewportSize = new() { Width = width, Height = height },
                IsMobile = false,
                HasTouch = false
            });
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(20_000);
            var portal = new PortalPage(page);
            await portal.GotoAgentChatAsync(_fixture.GatewayBaseUrl, data.AgentId);
            await SetPreferencesAsync(page, true, theme);
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.Load });
            await portal.EnsureSidebarOpenAsync();
            await Expect(Trigger(page)).ToContainTextAsync("Chat");
            await page.WaitForFunctionAsync("theme => (document.documentElement.getAttribute('data-theme') || 'dark') === theme", theme);
            await Expect(page.GetByTestId("agent-select")).ToHaveValueAsync(data.AgentId);
            foreach (var agent in _fixture.AgentIds)
                await Expect(page.GetByTestId("agent-select").Locator($"option[value='{agent}']")).ToHaveCountAsync(1);
            await Expect(page.GetByTestId("new-conversation-btn")).ToBeVisibleAsync();
            await Expect(page.GetByTestId("conversation-group-pinned")).ToBeVisibleAsync();
            await Expect(page.GetByTestId("conversation-group-conversations")).ToBeVisibleAsync();
            await ExpandCustomSectionAsync(page, data);
            await AssertPeerGroupsAndBuiltInMenusAsync(page, data);
            var section = CustomSection(page, data);
            await Expect(section.GetByTestId("section-name")).ToHaveTextAsync(data.SectionName);
            await Expect(CustomMemberLink(page, data).Locator(".conversation-list-item-title")).ToHaveTextAsync(data.MemberTitle);
            await Expect(page.GetByTestId("conversation-group-conversations")
                .Locator($"[data-conversation-id='{data.MemberId}']")).ToHaveCountAsync(0);
            await Expect(page.Locator("[data-testid='focused-section-content'][data-section='chat']")).ToBeVisibleAsync();
            await Expect(page.GetByTestId("nav-agents")).ToHaveCountAsync(0);
            await Expect(page.GetByTestId("client-build-info")).ToBeVisibleAsync();
            Assert.False(new Uri(page.Url).AbsolutePath.StartsWith("/mobile", StringComparison.Ordinal));
            var stem = $"{theme}-{width}x{height}";
            var content = page.GetByTestId("focused-section-content");
            var chatScroll = content.Locator(".focused-chat-scroll");
            var agentSelector = page.GetByTestId("agent-select");
            var conversationHeader = content.Locator(".agent-conversation-list-header");
            var newConversation = page.GetByTestId("new-conversation-btn");
            var newSection = page.GetByTestId("section-add-btn");
            await Expect(chatScroll).ToHaveCountAsync(1);
            await Expect(chatScroll).ToBeVisibleAsync();
            await Expect(conversationHeader).ToHaveCountAsync(1);
            // Expanding the custom section can auto-scroll its controls into view. Reset only
            // as capture/input setup; scripted positioning is never evidence of user scrolling.
            await chatScroll.EvaluateAsync("el => { el.scrollTop = 0; }");
            Assert.Equal(0, await chatScroll.EvaluateAsync<double>("el => el.scrollTop"));
            Assert.Equal(0, await content.EvaluateAsync<double>("el => el.scrollTop"));
            await Expect(agentSelector).ToBeInViewportAsync(new() { Ratio = 1 });
            await Expect(conversationHeader).ToBeInViewportAsync(new() { Ratio = 1 });
            await Expect(newConversation).ToBeInViewportAsync(new() { Ratio = 1 });
            await Expect(newSection).ToBeInViewportAsync(new() { Ratio = 1 });
            captures.Add(await CaptureAsync(page, artifacts, stem + "-chat-populated", invocation));

            var pickerBefore = await RequiredBoxAsync(Trigger(page));
            var documentBefore = await page.EvaluateAsync<double>("window.scrollY");
            var bounds = await RequiredBoxAsync(content);
            Assert.True(bounds.Height > 100 && bounds.Y >= pickerBefore.Y + pickerBefore.Height - 1,
                "Focused content must occupy the remaining area beneath the picker.");
            Assert.True(bounds.Y + bounds.Height <= height, "Focused content extends below the desktop viewport.");
            // Intentional replacement: Chat overflow and wheel movement belong to the inner
            // scroller, not the now overflow-hidden outer focused-section-content geometry.
            Assert.True(await chatScroll.EvaluateAsync<bool>("el => el.scrollHeight > el.clientHeight + 20"),
                "Seeded conversations must actually overflow the inner Chat scroll region.");
            var agentBefore = await RequiredBoxAsync(agentSelector);
            var headerBefore = await RequiredBoxAsync(conversationHeader);
            var newBefore = await RequiredBoxAsync(newConversation);
            var newSectionBefore = await RequiredBoxAsync(newSection);
            await chatScroll.HoverAsync();
            Assert.Equal(0, await chatScroll.EvaluateAsync<double>("el => el.scrollTop"));
            await page.Mouse.WheelAsync(0, 1200);
            await page.WaitForFunctionAsync("() => document.querySelector('[data-testid=focused-section-content][data-section=chat] .focused-chat-scroll').scrollTop > 0");
            Assert.True(await chatScroll.EvaluateAsync<bool>("el => el.scrollTop > 0"),
                "A real wheel event must move the inner Chat scroller.");
            Assert.Equal(0, await content.EvaluateAsync<double>("el => el.scrollTop"));
            AssertStationaryBox(agentBefore, await RequiredBoxAsync(agentSelector));
            AssertStationaryBox(headerBefore, await RequiredBoxAsync(conversationHeader));
            AssertStationaryBox(newBefore, await RequiredBoxAsync(newConversation));
            AssertStationaryBox(newSectionBefore, await RequiredBoxAsync(newSection));
            var pickerAfter = await RequiredBoxAsync(Trigger(page));
            Assert.InRange(Math.Abs(pickerAfter.Y - pickerBefore.Y), 0, 1);
            Assert.InRange(Math.Abs(pickerAfter.X - pickerBefore.X), 0, 1);
            Assert.Equal(documentBefore, await page.EvaluateAsync<double>("window.scrollY"));
            await Expect(Trigger(page)).ToBeInViewportAsync();
            captures.Add(await CaptureAsync(page, artifacts, stem + "-chat-scrolled", invocation));

            // Reset is setup only. Reach the region through native sequential navigation from
            // New conversation through New section; do not programmatically focus or scroll the region.
            await chatScroll.EvaluateAsync("el => { el.scrollTop = 0; }");
            Assert.Equal(0, await chatScroll.EvaluateAsync<double>("el => el.scrollTop"));
            await newConversation.FocusAsync();
            await Expect(newConversation).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Tab");
            await Expect(newSection).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Tab");
            await Expect(chatScroll).ToBeFocusedAsync();
            Assert.Equal(0, await chatScroll.EvaluateAsync<double>("el => el.scrollTop"));
            await page.Keyboard.PressAsync("PageDown");
            await page.WaitForFunctionAsync("() => document.querySelector('[data-testid=focused-section-content][data-section=chat] .focused-chat-scroll').scrollTop > 0");
            Assert.True(await chatScroll.EvaluateAsync<bool>("el => el.scrollTop > 0"),
                "Native PageDown on the tab-reached region must move the inner Chat scroller.");
            await Expect(chatScroll).ToBeFocusedAsync();
            Assert.Equal(0, await content.EvaluateAsync<double>("el => el.scrollTop"));
            Assert.Equal(documentBefore, await page.EvaluateAsync<double>("window.scrollY"));
            AssertStationaryBox(pickerBefore, await RequiredBoxAsync(Trigger(page)));
            AssertStationaryBox(agentBefore, await RequiredBoxAsync(agentSelector));
            AssertStationaryBox(headerBefore, await RequiredBoxAsync(conversationHeader));
            AssertStationaryBox(newBefore, await RequiredBoxAsync(newConversation));
            AssertStationaryBox(newSectionBefore, await RequiredBoxAsync(newSection));
            await Expect(agentSelector).ToBeInViewportAsync(new() { Ratio = 1 });
            await Expect(conversationHeader).ToBeInViewportAsync(new() { Ratio = 1 });
            await Expect(newConversation).ToBeInViewportAsync(new() { Ratio = 1 });
            await Expect(newSection).ToBeInViewportAsync(new() { Ratio = 1 });

            await SelectSectionAsync(page, "agents");
            await Expect(page.GetByTestId("focused-section-content")).ToHaveAttributeAsync("data-section", "agents");
            await Expect(page.GetByTestId("agent-select")).ToHaveCountAsync(0);
            foreach (var agent in _fixture.AgentIds)
                await Expect(page.GetByTestId($"subnav-agents-{agent}")).ToBeVisibleAsync();
            await Expect(page.GetByTestId("subnav-agents-new")).ToBeVisibleAsync();
            captures.Add(await CaptureAsync(page, artifacts, stem + "-agents-populated", invocation));

            await SelectSectionAsync(page, "chat");
            await page.GetByTestId("agent-select").SelectOptionAsync(_fixture.AgentIds[1]);
            await Expect(page.GetByTestId("agent-select")).ToHaveValueAsync(_fixture.AgentIds[1]);
            await page.GetByTestId("agent-select").SelectOptionAsync(data.AgentId);
            await Expect(page.GetByTestId("agent-select")).ToHaveValueAsync(data.AgentId);
            await ExpandCustomSectionAsync(page, data);
            await Expect(CustomMemberLink(page, data).Locator(".conversation-list-item-title")).ToHaveTextAsync(data.MemberTitle);
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.Load });
            await portal.EnsureSidebarOpenAsync();
            await Expect(Trigger(page)).ToContainTextAsync("Chat");
            await ExpandCustomSectionAsync(page, data);
            await Expect(CustomMemberLink(page, data).Locator(".conversation-list-item-title")).ToHaveTextAsync(data.MemberTitle);
            captures.Add(await CaptureAsync(page, artifacts, stem + "-chat-custom-section-preserved", invocation));
        }
        finally
        {
            await CleanupAsync(http, data);
        }

        // Emit only after all captures, assertions, browser disposal and cleanup succeed.
        Assert.Equal(4, captures.Count);
        var method = $"{typeof(FocusedSidebarBrowserTests).FullName}.{nameof(FocusedSidebar_PopulatedChatAndAgents_ScrollIndependentlyWithFixedPicker)}";
        _output.WriteLine("BOTNEXUS_BROWSER_INVOCATION_V1 " + JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            runId = invocation.RunId,
            nonce = invocation.Nonce,
            method,
            arguments = new object[] { theme, width, height },
            caseId = $"{method}[theme={theme};width={width};height={height}]",
            captures
        }));
    }

    [Fact]
    public async Task FocusedSidebar_SettingsKeyboardDeepLinksAndHistory_PreserveSelectionAndPreference()
    {
        AssertFixture();
        var invocation = new InvocationContext(
            Environment.GetEnvironmentVariable("RUN_ID")
                ?? throw new InvalidOperationException("Browser invocation evidence requires RUN_ID."),
            Guid.NewGuid().ToString("N"));
        var artifacts = ArtifactDirectory(invocation);
        var captures = new List<string>();
        using var http = new HttpClient { BaseAddress = new Uri(_fixture.GatewayBaseUrl) };
        var data = await SeedAsync(http, 3);
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await PlaywrightBootstrap.LaunchChromiumAsync(playwright);
            await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 900 } });
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(20_000);
            var portal = new PortalPage(page);
            await portal.GotoAgentChatAsync(_fixture.GatewayBaseUrl, data.AgentId);
            await Expect(Trigger(page)).ToHaveCountAsync(0);
            await ExpandCustomSectionAsync(page, data);
            await AssertPeerGroupsAndBuiltInMenusAsync(page, data);
            await SetPreferencesAsync(page, true, "dark");
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.Load });
            await portal.EnsureSidebarOpenAsync();
            await Expect(Trigger(page)).ToContainTextAsync("Chat");

            var trigger = Trigger(page);
            await trigger.FocusAsync();
            await trigger.PressAsync("ArrowDown");
            var menu = page.GetByRole(AriaRole.Menu, new() { Name = "Sidebar section", Exact = true });
            await Expect(menu).ToBeVisibleAsync();
            var chatOption = menu.Locator("[data-section='chat']");
            await Expect(chatOption).ToBeFocusedAsync();
            await Expect(chatOption).ToHaveAttributeAsync("aria-checked", "true");
            foreach (var option in await menu.GetByRole(AriaRole.Menuitemradio).AllAsync())
                await Expect(option.Locator("svg")).ToHaveCountAsync(1);
            var ordered = await menu.GetByRole(AriaRole.Menuitemradio).EvaluateAllAsync<string[]>(
                "els => els.map(el => el.dataset.section)");
            var chatIndex = Array.IndexOf(ordered, "chat");
            Assert.True(chatIndex >= 0 && ordered.Length > 1);
            await page.Keyboard.PressAsync("ArrowDown");
            await Expect(menu.Locator($"[data-section='{ordered[(chatIndex + 1) % ordered.Length]}']")).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("ArrowUp");
            await Expect(chatOption).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("End");
            await Expect(menu.Locator($"[data-section='{ordered[^1]}']")).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Home");
            await Expect(menu.Locator($"[data-section='{ordered[0]}']")).ToBeFocusedAsync();
            captures.Add(await CaptureAsync(page, artifacts, "keyboard-icon-picker-open", invocation));
            await page.Keyboard.PressAsync("Escape");
            await Expect(menu).ToHaveCountAsync(0);
            await Expect(trigger).ToBeFocusedAsync();
            await Expect(trigger).ToHaveAttributeAsync("aria-expanded", "false");

            await trigger.PressAsync("ArrowDown");
            var agentsIndex = Array.IndexOf(ordered, "agents");
            Assert.True(agentsIndex >= 0);
            var steps = (agentsIndex - chatIndex + ordered.Length) % ordered.Length;
            for (var i = 0; i < steps; i++)
            {
                await page.Keyboard.PressAsync("ArrowDown");
                await Expect(menu.Locator($"[data-section='{ordered[(chatIndex + i + 1) % ordered.Length]}']")).ToBeFocusedAsync();
            }
            await Expect(menu.Locator("[data-section='agents']")).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(trigger).ToContainTextAsync("Agents");
            await Expect(trigger).ToBeFocusedAsync();
            await Expect(page.GetByTestId($"subnav-agents-{data.AgentId}")).ToBeVisibleAsync();
            captures.Add(await CaptureAsync(page, artifacts, "keyboard-agents-selection", invocation));

            await page.GoBackAsync();
            await Expect(trigger).ToContainTextAsync("Chat");
            await Expect(page.GetByTestId("agent-select")).ToHaveValueAsync(data.AgentId);
            await page.GoForwardAsync();
            await Expect(trigger).ToContainTextAsync("Agents");
            var deepLink = $"{_fixture.GatewayBaseUrl}/agent/{data.AgentId}/conversation/{data.MemberId}?tab=chat#messages";
            await page.GotoAsync(deepLink, new() { WaitUntil = WaitUntilState.Load });
            await portal.EnsureSidebarOpenAsync();
            await Expect(trigger).ToContainTextAsync("Chat");
            await Expect(page.GetByTestId("agent-select")).ToHaveValueAsync(data.AgentId);
            await ExpandCustomSectionAsync(page, data);
            await Expect(CustomMemberLink(page, data).Locator(".conversation-list-item-title")).ToHaveTextAsync(data.MemberTitle);
            Assert.Contains(data.MemberId, page.Url, StringComparison.Ordinal);
            captures.Add(await CaptureAsync(page, artifacts, "keyboard-chat-deep-link", invocation));

            await SetPreferencesAsync(page, false, "dark");
            await Expect(trigger).ToHaveCountAsync(0);
            await portal.EnsureSidebarOpenAsync();
            await ExpandCustomSectionAsync(page, data);
            await AssertPeerGroupsAndBuiltInMenusAsync(page, data);
            await Expect(page.GetByTestId("nav-agents")).ToBeVisibleAsync();
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.Load });
            await portal.EnsureSidebarOpenAsync();
            await Expect(trigger).ToHaveCountAsync(0);
            await SetPreferencesAsync(page, true, "dark");
            await Expect(trigger).ToContainTextAsync("Chat");
            Assert.Contains(data.MemberId, page.Url, StringComparison.Ordinal);
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.Load });
            await portal.EnsureSidebarOpenAsync();
            await Expect(trigger).ToContainTextAsync("Chat");
            Assert.Contains(data.MemberId, page.Url, StringComparison.Ordinal);
            captures.Add(await CaptureAsync(page, artifacts, "keyboard-chat-toggle-reload-preserved", invocation));
        }
        finally
        {
            await CleanupAsync(http, data);
        }

        // The fact owns an empty typed argument tuple and its method is its case ID.
        Assert.Equal(4, captures.Count);
        var method = $"{typeof(FocusedSidebarBrowserTests).FullName}.{nameof(FocusedSidebar_SettingsKeyboardDeepLinksAndHistory_PreserveSelectionAndPreference)}";
        _output.WriteLine("BOTNEXUS_BROWSER_INVOCATION_V1 " + JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            runId = invocation.RunId,
            nonce = invocation.Nonce,
            method,
            arguments = Array.Empty<object>(),
            caseId = method,
            captures
        }));
    }

    private void AssertFixture()
    {
        Assert.True(_fixture.Succeeded, $"Candidate fixture failed: {_fixture.Error}\n{string.Join('\n', _fixture.Log)}");
        Assert.True(_fixture.GatewayPort > 0);
        Assert.True(Directory.Exists(_fixture.Home));
        Assert.True(Path.GetRelativePath(_fixture.SandboxRoot, _fixture.Home) == "home");
        Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BOTNEXUS_DATA_DIR")),
            "Remote fixture inherits BOTNEXUS_DATA_DIR; refuse an unverified external data root.");
    }

    private static ILocator Trigger(IPage page) => page.GetByTestId("focused-section-trigger");

    private static async Task SetPreferencesAsync(IPage page, bool focused, string theme)
    {
        // Navigation opens the sidebar; on narrow viewports its overlay covers the banner.
        var overlay = page.Locator(".sidebar-overlay");
        if (await overlay.IsVisibleAsync())
        {
            var overlayBox = await RequiredBoxAsync(overlay);
            var sidebarBox = await RequiredBoxAsync(page.Locator(".main-sidebar"));
            Assert.True(overlayBox.X + overlayBox.Width - 8 > sidebarBox.X + sidebarBox.Width,
                "Overlay dismissal point must be exposed to the right of the sidebar.");
            await overlay.ClickAsync(new()
            {
                Position = new() { X = overlayBox.Width - 8, Y = overlayBox.Height - 8 }
            });
        }
        await Expect(overlay).ToBeHiddenAsync();
        await page.GetByTestId("banner-settings-btn").ClickAsync();
        var toggle = page.GetByTestId("focused-sidebar-toggle");
        await Expect(toggle).ToBeVisibleAsync();
        await toggle.SetCheckedAsync(focused);
        await page.GetByTestId("theme-select").SelectOptionAsync(theme);
        await page.WaitForFunctionAsync("expected => { const p = JSON.parse(localStorage.getItem('botnexus.portal.prefs') || '{}'); return p.FocusedSidebar === expected.focused && p.Theme === expected.theme; }",
            new { focused, theme });
        await page.GetByTestId("portal-settings-close").ClickAsync();
        await Expect(page.Locator(".portal-settings-panel")).ToHaveCountAsync(0);
    }

    private static async Task SelectSectionAsync(IPage page, string section)
    {
        await Trigger(page).ClickAsync();
        var option = page.Locator($"#focused-section-menu [role='menuitemradio'][data-section='{section}']");
        await Expect(option.Locator("svg")).ToHaveCountAsync(1);
        await option.ClickAsync();
        await Expect(page.GetByTestId("focused-section-content")).ToHaveAttributeAsync("data-section", section);
    }

    private static ILocator CustomSection(IPage page, SeedData data) =>
        page.Locator($".conversation-list-scroll > .conversation-group[data-section-id='{data.SectionId}']");

    private static ILocator CustomMemberLink(IPage page, SeedData data) =>
        CustomSection(page, data).Locator($"[data-conversation-id='{data.MemberId}'] a.conversation-list-item-btn");

    private static async Task ExpandCustomSectionAsync(IPage page, SeedData data)
    {
        var section = CustomSection(page, data);
        var toggle = section.GetByTestId("section-collapse-toggle");
        await Expect(toggle).ToBeAttachedAsync();
        // Only a confirmed collapsed peer needs a click; there is no parent group to expand.
        if (await toggle.GetAttributeAsync("aria-expanded") == "false")
            await toggle.ClickAsync();
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(section.GetByTestId("conversation-section-body")).ToBeVisibleAsync();
        await Expect(CustomMemberLink(page, data)).ToHaveCountAsync(1);
        await Expect(CustomMemberLink(page, data)).ToHaveAttributeAsync("href",
            $"agent/{data.AgentId}/conversation/{data.MemberId}");
    }

    private static async Task AssertPeerGroupsAndBuiltInMenusAsync(IPage page, SeedData data)
    {
        var scroll = page.Locator(".conversation-list-scroll");
        await Expect(scroll).ToHaveCountAsync(1);
        await Expect(CustomSection(page, data)).ToHaveCountAsync(1);
        await Expect(page.GetByTestId("sections-group-toggle")).ToHaveCountAsync(0);
        await Expect(page.GetByTestId("chat-sections-body")).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "My sections", Exact = true })).ToHaveCountAsync(0);
        foreach (var key in new[] { "pinned", "scheduled", "automated", "conversations" })
        {
            var group = scroll.Locator($":scope > .conversation-group[data-testid='conversation-group-{key}']");
            await Expect(group).ToHaveCountAsync(1);
            var trigger = group.GetByTestId("section-actions-trigger");
            await Expect(trigger).ToBeEnabledAsync();
            await trigger.ClickAsync();
            var menu = group.GetByRole(AriaRole.Menu);
            await Expect(menu).ToBeVisibleAsync();
            await Expect(menu.GetByRole(AriaRole.Menuitem)).ToHaveCountAsync(2);
            await Expect(menu.GetByTestId("section-move-up")).ToHaveCountAsync(1);
            await Expect(menu.GetByTestId("section-move-down")).ToHaveCountAsync(1);
            await Expect(menu.GetByTestId("section-rename-btn")).ToHaveCountAsync(0);
            await Expect(menu.GetByTestId("section-delete-btn")).ToHaveCountAsync(0);
            // Inspect and dismiss only: no reorder, rename, delete or creation mutation.
            await group.GetByTestId("section-menu-dismiss").ClickAsync();
            await Expect(menu).ToHaveCountAsync(0);
            await Expect(trigger).ToHaveAttributeAsync("aria-expanded", "false");
        }
    }

    private static async Task<LocatorBoundingBoxResult> RequiredBoxAsync(ILocator locator) =>
        await locator.BoundingBoxAsync() ?? throw new InvalidOperationException("Expected a rendered bounding box.");

    private static void AssertStationaryBox(LocatorBoundingBoxResult before, LocatorBoundingBoxResult after)
    {
        Assert.InRange(Math.Abs(after.X - before.X), 0, 1);
        Assert.InRange(Math.Abs(after.Y - before.Y), 0, 1);
        Assert.InRange(Math.Abs(after.Width - before.Width), 0, 1);
        Assert.InRange(Math.Abs(after.Height - before.Height), 0, 1);
    }

    private sealed record InvocationContext(string RunId, string Nonce);

    private static string ArtifactDirectory(InvocationContext invocation)
    {
        var runId = invocation.RunId;
        if (!OperatingSystem.IsLinux() || !Regex.IsMatch(runId, @"\A[A-Za-z0-9][A-Za-z0-9-]{0,127}\z"))
            throw new InvalidOperationException("Focused sidebar browser evidence requires the isolated Linux remote runner and RUN_ID.");
        var root = Path.Combine("/work", runId, "artifacts");
        if (!Directory.Exists(root))
            throw new InvalidOperationException("Remote runner artifact root is missing; refuse uncollected screenshots.");
        var directory = Path.Combine(root, "screenshots-5095");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private async Task<string> CaptureAsync(IPage page, string directory, string name, InvocationContext invocation)
    {
        var path = Path.Combine(directory, name + ".png");
        var content = page.GetByTestId("focused-section-content");
        var section = await content.GetAttributeAsync("data-section");
        var scrollOwnerSelector = section == "chat"
            ? "[data-testid='focused-section-content'][data-section='chat'] .focused-chat-scroll"
            : "[data-testid='focused-section-content']";
        var scrollOwner = page.Locator(scrollOwnerSelector);
        await Expect(scrollOwner).ToHaveCountAsync(1);
        await page.ScreenshotAsync(new() { Path = path, FullPage = true });
        var evidence = new
        {
            invocation = new { schemaVersion = 1, runId = invocation.RunId, nonce = invocation.Nonce },
            route = page.Url,
            viewport = page.ViewportSize,
            build = await page.GetByTestId("client-build-info").InnerTextAsync(),
            theme = await page.Locator("html").GetAttributeAsync("data-theme") ?? "dark",
            section,
            contentGeometry = await content.EvaluateAsync<JsonElement>(
                "el => ({scrollTop: el.scrollTop, scrollHeight: el.scrollHeight, clientHeight: el.clientHeight})"),
            scrollOwner = new
            {
                selector = scrollOwnerSelector,
                geometry = await scrollOwner.EvaluateAsync<JsonElement>(
                    "el => ({scrollTop: el.scrollTop, scrollHeight: el.scrollHeight, clientHeight: el.clientHeight})")
            }
        };
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(evidence));
        Assert.True(new FileInfo(path).Length > 0, "Screenshot must contain actual image bytes.");
        _output.WriteLine($"Screenshot: {path}\nEvidence: {JsonSerializer.Serialize(evidence)}");
        // Charter paths are relative to the collected artifact root, not the runner filesystem.
        return $"screenshots-5095/{name}.json";
    }

    private sealed record SeedData(string AgentId, string SectionId, string SectionName,
        string MemberId, string MemberTitle, IReadOnlyList<string> ConversationIds);

    private async Task<SeedData> SeedAsync(HttpClient http, int count)
    {
        var agent = _fixture.AgentIds[0];
        var prefix = "Focused-5095-" + Guid.NewGuid().ToString("N")[..8];
        var ids = new List<string>();
        var memberTitle = prefix + " custom member";
        for (var i = 0; i < count; i++)
        {
            using var response = await http.PostAsJsonAsync("/api/conversations", new
            {
                agentId = agent,
                title = i == 0 ? memberTitle : $"{prefix} conversation {i:D2}"
            });
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            ids.Add(json.GetProperty("conversationId").GetString()
                ?? throw new InvalidOperationException("Candidate API omitted conversation identity."));
        }
        using (var pinned = await http.PostAsync($"/api/conversations/{ids[1]}/pin", null))
            pinned.EnsureSuccessStatusCode();
        using var sectionResponse = await http.PostAsJsonAsync($"/api/agents/{agent}/sections", new { name = prefix });
        sectionResponse.EnsureSuccessStatusCode();
        var sectionJson = await sectionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sectionId = sectionJson.GetProperty("sectionId").GetString()
            ?? throw new InvalidOperationException("Candidate API omitted section identity.");
        using (var assignment = await http.PutAsync($"/api/agents/{agent}/sections/{sectionId}/conversations/{ids[0]}", null))
            assignment.EnsureSuccessStatusCode();
        return new(agent, sectionId, prefix, ids[0], memberTitle, ids);
    }

    private static async Task CleanupAsync(HttpClient http, SeedData data)
    {
        using (var section = await http.DeleteAsync($"/api/agents/{data.AgentId}/sections/{data.SectionId}"))
            section.EnsureSuccessStatusCode();
        foreach (var id in data.ConversationIds)
        {
            using var conversation = await http.DeleteAsync($"/api/conversations/{id}");
            conversation.EnsureSuccessStatusCode();
        }
    }
}
