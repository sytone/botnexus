using Microsoft.Playwright;

namespace BotNexus.Integration.E2E.Tests.PageObjects;

/// <summary>
/// Page object for the /agents management page.
/// </summary>
public sealed class AgentsPage
{
    public IPage Page { get; }

    // ── Toolbar ───────────────────────────────────────────────────────────
    public ILocator AddAgentBtn     => Page.Locator("button[title='Add a new agent']").First;
    public ILocator ReloadBtn       => Page.Locator("button[title='Refresh agent list']").First;
    public ILocator StatusMessage   => Page.Locator(".agents-status").First;

    // ── Table ─────────────────────────────────────────────────────────────
    public ILocator AgentTable      => Page.Locator(".agents-table").First;
    public ILocator AgentRows       => Page.Locator(".agents-table tbody tr");

    // ── Form ──────────────────────────────────────────────────────────────
    public ILocator FormCard        => Page.Locator(".agents-form-card").First;
    public ILocator AgentIdInput    => Page.Locator("#agent-id-input").First;
    public ILocator DisplayNameInput => Page.Locator("#display-name-input").First;
    public ILocator DescriptionInput => Page.Locator("#description-input").First;
    public ILocator ProviderSelect  => Page.Locator("#provider-input").First;
    public ILocator ModelSelect     => Page.Locator("#model-id-input").First;
    public ILocator SystemPromptInput => Page.Locator("#system-prompt-input").First;
    public ILocator SaveBtn         => Page.Locator(".agents-form-card button.primary").First;
    public ILocator CancelFormBtn   => Page.Locator(".agents-form-actions button:not(.primary)").First;
    public ILocator FormError       => Page.Locator(".agents-form-error").First;
    public ILocator DirtyIndicator  => Page.Locator(".agents-dirty-indicator").First;

    // ── Delete confirmation ───────────────────────────────────────────────
    public ILocator DeleteConfirmDialog => Page.Locator(".agents-confirm-dialog").First;
    public ILocator DeleteConfirmBtn    => Page.Locator(".agents-confirm-dialog .danger").First;
    public ILocator DeleteCancelBtn     => Page.Locator(".agents-confirm-dialog button:not(.danger)").First;

    // ── Empty state ───────────────────────────────────────────────────────
    public ILocator EmptyState      => Page.Locator(".agents-empty").First;

    public AgentsPage(IPage page) => Page = page;

    /// <summary>Navigate to /agents and wait for the page to load.</summary>
    public async Task GotoAsync(string baseUrl)
    {
        await Page.GotoAsync($"{baseUrl}/agents", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 60_000,
        });
        // Wait for either the table or the empty state
        await Page.Locator(".agents-page").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000,
        });
    }

    /// <summary>
    /// Fill the agent form and save. Assumes the form is already open.
    /// </summary>
    public async Task FillAndSaveFormAsync(string agentId, string displayName, string provider, string model)
    {
        await AgentIdInput.FillAsync(agentId);
        await DisplayNameInput.FillAsync(displayName);
        // Provider — try select first; fall back to input
        try
        {
            await ProviderSelect.SelectOptionAsync(new SelectOptionValue { Value = provider });
            // Wait for model list to populate
            await Page.WaitForTimeoutAsync(500);
        }
        catch
        {
            await ProviderSelect.FillAsync(provider);
        }

        // Model — try select first; fall back to input
        try
        {
            await ModelSelect.SelectOptionAsync(new SelectOptionValue { Value = model });
        }
        catch
        {
            await ModelSelect.FillAsync(model);
        }

        await SaveBtn.ClickAsync();
    }

    /// <summary>Get the agent IDs visible in the table.</summary>
    public async Task<IReadOnlyList<string>> GetAgentIdsAsync()
    {
        var cells = Page.Locator(".agents-table .agents-id");
        return await cells.AllInnerTextsAsync();
    }

    /// <summary>Click the edit button for a given agent row.</summary>
    public async Task ClickEditAsync(string agentId)
    {
        await Page.Locator($"button[aria-label='Edit {agentId}']").First.ClickAsync();
    }

    /// <summary>Click the delete button for a given agent row.</summary>
    public async Task ClickDeleteAsync(string agentId)
    {
        await Page.Locator($"button[aria-label='Delete {agentId}']").First.ClickAsync();
    }
}
