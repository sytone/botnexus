using Microsoft.Playwright;
using BotNexus.Integration.E2E.Tests.PageObjects;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Tests for sidebar navigation and agent switching.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class SidebarNavigationTests
{
    private readonly NewUserExperienceFixture _fx;

    public SidebarNavigationTests(NewUserExperienceFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task AgentDropdown_ContainsAllProvisionedAgents()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;
        var (page, portal, chat) = await PortalTestHelpers.NewChatPageAsync(browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        // Sidebar is opened by NewChatPageAsync -> GotoAgentChatAsync -> EnsureSidebarOpenAsync
        var dropdown = portal.AgentDropdown;
        await dropdown.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        var options = await dropdown.Locator("option:not([value=''])").AllInnerTextsAsync();
        foreach (var agentId in _fx.AgentIds)
            Assert.Contains(options, o => o.Contains(agentId, StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task SwitchAgent_LoadsAgentPanel_UpdatesUrl()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;
        var (page, portal, chat) = await PortalTestHelpers.NewChatPageAsync(browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        var targetAgent = _fx.AgentIds[1];
        await portal.AgentDropdown.SelectOptionAsync(targetAgent);

        await page.WaitForURLAsync(
            url => url.Contains(targetAgent, StringComparison.OrdinalIgnoreCase),
            new PageWaitForURLOptions { Timeout = 15_000 });

        // agent-panel is in the sidebar which is already open; wait Attached then Visible
        var agentPanel = page.Locator("[data-testid='agent-panel']").First;
        await agentPanel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 10_000 });
    }

    [SkippableFact]
    public async Task AgentTabs_AllFourTabsPresent_SwitchCorrectly()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;
        var (page, portal, chat) = await PortalTestHelpers.NewChatPageAsync(browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        // Scope tab interactions to the visible agent-panel to avoid strict-mode
        // violations (multiple agents render multiple panels, only one is active/open).
        var activePanel = page.Locator("[data-testid='agent-panel']").First;
        await activePanel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 15_000 });

        var tabs = activePanel.Locator(".agent-panel-tab");
        await tabs.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        var tabLabels = await tabs.AllInnerTextsAsync();
        var flatLabels = string.Join(" ", tabLabels).ToLowerInvariant();

        Assert.True(flatLabels.Contains("conversation"), "Conversation tab missing");
        Assert.True(flatLabels.Contains("workspace"), "Workspace tab missing");
        Assert.True(flatLabels.Contains("reports"), "Reports tab missing");
        Assert.True(flatLabels.Contains("canvas"), "Canvas tab missing");

        // Click Workspace tab and verify selection — scope to active panel
        var workspaceTab = activePanel.Locator(".agent-panel-tab").Filter(new LocatorFilterOptions { HasTextString = "Workspace" }).First;
        await workspaceTab.ClickAsync();

        var workspaceTabSelected = activePanel.Locator("[data-tab='workspace']").First;
        var ariaSelected = await workspaceTabSelected.GetAttributeAsync("aria-selected");
        Assert.True(
            "true".Equals(ariaSelected, StringComparison.OrdinalIgnoreCase) || await workspaceTabSelected.EvaluateAsync<bool>("el => el.classList.contains('active')"),
            $"Workspace tab not selected after click. aria-selected='{ariaSelected}'");

        // Switch back to Conversation tab
        var convTab = activePanel.Locator(".agent-panel-tab").Filter(new LocatorFilterOptions { HasTextString = "Conversation" }).First;
        await convTab.ClickAsync();

        var convTabSelected = activePanel.Locator("[data-tab='conversation']").First;
        ariaSelected = await convTabSelected.GetAttributeAsync("aria-selected");
        Assert.True(
            "true".Equals(ariaSelected, StringComparison.OrdinalIgnoreCase) || await convTabSelected.EvaluateAsync<bool>("el => el.classList.contains('active')"),
            $"Conversation tab not selected after click. aria-selected='{ariaSelected}'");
    }

    [SkippableFact]
    public async Task SidebarChatLink_NavigatesToChatPage()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;
        var (page, portal, chat) = await PortalTestHelpers.NewChatPageAsync(browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await page.GotoAsync($"{_fx.GatewayBaseUrl}/agents");
        await page.WaitForLoadStateAsync(LoadState.Load);

        // After navigation the sidebar state is reset — re-open it
        await portal.EnsureSidebarOpenAsync();

        await portal.SidebarChatLink.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await portal.SidebarChatLink.ClickAsync();

        await page.WaitForURLAsync(
            url => url.Contains("/chat") || url.EndsWith("/"),
            new PageWaitForURLOptions { Timeout = 10_000 });
    }

    [SkippableFact]
    public async Task SidebarConfigLink_NavigatesToConfigurationPage()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;
        var (page, portal, chat) = await PortalTestHelpers.NewChatPageAsync(browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        await portal.SidebarConfigLink.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await portal.SidebarConfigLink.ClickAsync();

        await page.WaitForURLAsync(
            url => url.Contains("configuration", StringComparison.OrdinalIgnoreCase),
            new PageWaitForURLOptions { Timeout = 10_000 });

        var subNav = page.Locator(".sidebar-subnav");
        await subNav.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        var subItems = await subNav.Locator(".sidebar-subnav-item").AllInnerTextsAsync();
        var flatItems = string.Join(" ", subItems).ToLowerInvariant();
        Assert.True(flatItems.Contains("gateway"), "Gateway sub-nav item missing");
        Assert.True(flatItems.Contains("providers"), "Providers sub-nav item missing");
        Assert.True(flatItems.Contains("channels"), "Channels sub-nav item missing");
    }

    [SkippableFact]
    public async Task AgentStatusLabel_ShowsIdleWhenNotStreaming()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;
        var (page, portal, chat) = await PortalTestHelpers.NewChatPageAsync(browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        var statusLabel = page.Locator(".agent-panel-status").First;
        await statusLabel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        var text = await statusLabel.InnerTextAsync();
        Assert.Equal("Idle", text.Trim());
    }

    [SkippableFact]
    public async Task BannerTitle_ReadsBotnexus()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;
        var (page, portal, chat) = await PortalTestHelpers.NewChatPageAsync(browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        var title = await portal.BannerTitle.InnerTextAsync();
        Assert.True(title.Contains("BotNexus", StringComparison.OrdinalIgnoreCase),
            $"Banner title should contain BotNexus, got: {title}");
    }

    [SkippableFact]
    public async Task BannerSettingsButton_HasMeaningfulContent_NotLiteralX()
    {
        // Regression test for issue #630: settings button renders 'x' instead of icon
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");
        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);
        await using var _ = browser!;
        var (page, portal, chat) = await PortalTestHelpers.NewChatPageAsync(browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        var settingsBtn = portal.BannerSettingsBtn;
        await settingsBtn.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        var content = await settingsBtn.InnerTextAsync();
        Assert.True(content.Trim() != "x",
            "Settings button renders literal 'x' -- see issue #630. Expected a gear/settings icon.");

        var btnTitle = await settingsBtn.GetAttributeAsync("title");
        Assert.NotNull(btnTitle);
        Assert.True(btnTitle!.Contains("setting", StringComparison.OrdinalIgnoreCase),
            $"Button title '{btnTitle}' should contain 'setting'.");
    }
}
