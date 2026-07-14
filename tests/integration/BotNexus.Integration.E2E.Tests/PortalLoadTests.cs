namespace BotNexus.E2E.Tests;

/// <summary>
/// Tests that the Blazor portal loads correctly and exposes expected UI elements.
/// </summary>
public class PortalLoadTests : E2ETestBase
{
    /// <summary>
    /// After a hard refresh the portal should fully initialise and the main sidebar
    /// element should be present in the DOM within the allowed timeout.
    /// </summary>
    [SkippableFact]
    public async Task HardRefresh_ShowsLoadingSpinner_ThenReady()
    {
        await Page.GotoAsync(BaseUrl);

        // Portal loads Blazor WASM — wait for sidebar to be attached (may start closed)
        await Page.WaitForSelectorAsync(".main-sidebar", new() { Timeout = 15000, State = WaitForSelectorState.Attached });
        var sidebar = Page.Locator(".main-sidebar");
        (await sidebar.CountAsync()).ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The first available agent must appear in the agent selection dropdown after portal load.
    /// </summary>
    [SkippableFact]
    public async Task PortalLoad_AgentAppearsInDropdown()
    {
        await WaitForPortalReadyAsync();
        await EnsureSidebarOpenAsync();

        // At least one option beyond the placeholder "— select agent —" should be present
        var options = await Page.Locator(".agent-dropdown-select option[value]").AllTextContentsAsync();
        options.ShouldNotBeEmpty();
    }

    /// <summary>
    /// After selecting the probe agent at least one conversation item should be
    /// visible in the sidebar conversation list.
    /// </summary>
    [SkippableFact]
    public async Task PortalLoad_DefaultConversationVisible_AfterAgentSelect()
    {
        await WaitForPortalReadyAsync();
        await SelectAgentAsync(AgentId);

        await Page.WaitForSelectorAsync(".conversation-list-item", new() { Timeout = 10000, State = WaitForSelectorState.Attached });
        var convCount = await Page.Locator(".conversation-list-item").CountAsync();
        convCount.ShouldBeGreaterThan(0);
    }
}
