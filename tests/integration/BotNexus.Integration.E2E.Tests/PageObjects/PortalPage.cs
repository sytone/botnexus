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
    /// Navigate to the portal root and wait for the Blazor SPA to hydrate.
    /// Sidebar starts closed by default (no localStorage in CI); opens it
    /// automatically so tests can interact with nav items.
    /// </summary>
    public async Task GotoAndWaitForLoadAsync(string baseUrl, TimeSpan? timeout = null)
    {
        var ms = (float)(timeout ?? TimeSpan.FromSeconds(60)).TotalMilliseconds;
        await Page.GotoAsync(baseUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = ms,
        });

        // Wait for the portal to finish its SignalR init phase.
        // Use Attached (not Visible) — sidebar items exist in the DOM even when closed.
        await Page.Locator(".sidebar-nav-item").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = ms,
        });

        await EnsureSidebarOpenAsync();
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

        // Wait for agent-panel to be attached then ensure sidebar is open.
        await Page.Locator("[data-testid='agent-panel']").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = ms,
        });

        await EnsureSidebarOpenAsync();
    }

    /// <summary>
    /// Ensure the sidebar is in the open state. The sidebar defaults to
    /// closed (no localStorage in CI) and must be opened before tests can
    /// interact with nav items, the agent dropdown, or conversation list.
    /// Clicks the burger button only when the sidebar is currently closed.
    /// </summary>
    public async Task EnsureSidebarOpenAsync()
    {
        var sidebar = Page.Locator(".main-sidebar").First;
        var isClosed = await sidebar.EvaluateAsync<bool>(
            "el => el.classList.contains('sidebar-closed')");
        if (isClosed)
        {
            await BurgerBtn.ClickAsync();
            // Wait for sidebar to transition to open
            await sidebar.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 5_000,
            });
        }
    }

    /// <summary>Select an agent from the sidebar dropdown.</summary>
    public async Task SelectAgentAsync(string agentId)
    {
        await EnsureSidebarOpenAsync();
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
        await EnsureSidebarOpenAsync();
        var items = Page.Locator("[data-testid='conversation-list-item'] .conversation-list-item-title");
        return await items.AllInnerTextsAsync();
    }
}
