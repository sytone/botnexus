using Microsoft.Playwright;

namespace BotNexus.Integration.E2E.Tests.PageObjects;

/// <summary>
/// Page object for the main BotNexus portal layout (sidebar + main canvas).
/// Encapsulates stable locators so individual test files don't embed raw
/// selectors — makes selector refactors a one-place change.
/// </summary>
public sealed class PortalPage
{
    public IPage Page { get; }

    // ── Sidebar ───────────────────────────────────────────────────────────
    public ILocator SidebarNav          => Page.Locator(".main-sidebar");
    public ILocator BurgerBtn           => Page.Locator(".burger-btn").First;
    public ILocator AgentDropdown       => Page.Locator(".agent-dropdown-select").First;
    public ILocator ConversationNewBtn  => Page.Locator(".conversation-new-btn").First;
    public ILocator ConversationList    => Page.Locator(".agent-conversation-list");
    public ILocator ConversationItems   => Page.Locator("[data-testid='conversation-list-item']");
    public ILocator SidebarChatLink     => Page.Locator(".sidebar-nav-item").Filter(new LocatorFilterOptions { HasTextString = "Chat" }).First;
    public ILocator SidebarAgentsLink   => Page.Locator(".sidebar-nav-item").Filter(new LocatorFilterOptions { HasTextString = "Agents" }).First;
    public ILocator SidebarConfigLink   => Page.Locator(".sidebar-nav-item").Filter(new LocatorFilterOptions { HasTextString = "Configuration" }).First;
    public ILocator ConnectionStatus    => Page.Locator(".sidebar-connection").First;
    public ILocator RestartGatewayBtn   => Page.Locator(".restart-btn").First;

    // ── Banner ────────────────────────────────────────────────────────────
    public ILocator BannerTitle         => Page.Locator(".banner-title").First;
    public ILocator BannerSettingsBtn   => Page.Locator(".banner-settings-btn").First;

    // ── Portal loading ────────────────────────────────────────────────────
    public ILocator PortalLoadSpinner   => Page.Locator(".portal-loading").First;
    public ILocator PortalLoadError     => Page.Locator(".portal-load-error").First;

    // ── Agent dashboard (home screen, no agent selected) ─────────────────
    public ILocator AgentDashboard      => Page.Locator(".agent-dashboard").First;
    public ILocator AgentCards          => Page.Locator(".agent-card");

    public PortalPage(IPage page) => Page = page;

    /// <summary>
    /// Navigate to the portal root and wait for the Blazor SPA to hydrate
    /// (portal-loading spinner disappears and at least one sidebar nav item is visible).
    /// </summary>
    public async Task GotoAndWaitForLoadAsync(string baseUrl, TimeSpan? timeout = null)
    {
        var ms = (float)(timeout ?? TimeSpan.FromSeconds(60)).TotalMilliseconds;
        await Page.GotoAsync(baseUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = ms,
        });

        // Wait for the portal to finish its SignalR init phase
        await Page.Locator(".sidebar-nav-item").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = ms,
        });
    }

    /// <summary>
    /// Navigate to a specific agent's chat page and wait for the chat panel.
    /// </summary>
    public async Task GotoAgentChatAsync(string baseUrl, string agentId, TimeSpan? timeout = null)
    {
        var ms = (float)(timeout ?? TimeSpan.FromSeconds(60)).TotalMilliseconds;
        await Page.GotoAsync($"{baseUrl}/chat/{agentId}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = ms,
        });

        await Page.Locator("[data-testid='agent-panel']").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = ms,
        });
    }

    /// <summary>Select an agent from the sidebar dropdown.</summary>
    public async Task SelectAgentAsync(string agentId)
    {
        await AgentDropdown.SelectOptionAsync(agentId);
        // Wait for conversation list to appear under the selected agent
        await ConversationList.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000,
        });
    }

    /// <summary>Get all conversation titles currently visible in the sidebar.</summary>
    public async Task<IReadOnlyList<string>> GetConversationTitlesAsync()
    {
        var items = Page.Locator("[data-testid='conversation-list-item'] .conversation-list-item-title");
        return await items.AllInnerTextsAsync();
    }
}
