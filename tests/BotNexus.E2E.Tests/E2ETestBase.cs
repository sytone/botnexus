using Microsoft.Playwright;

namespace BotNexus.E2E.Tests;

/// <summary>
/// Minimal representation of a gateway agent for agent discovery.
/// </summary>
internal sealed record AgentInfo(string? AgentId, string? DisplayName);

/// <summary>
/// Shared base fixture for Playwright E2E tests.
/// Skips all tests cleanly if the dev gateway is not running at localhost:5006.
/// </summary>
public abstract class E2ETestBase : IAsyncLifetime
{
    protected IPlaywright Playwright { get; private set; } = default!;
    protected IBrowser Browser { get; private set; } = default!;
    protected IBrowserContext Context { get; private set; } = default!;
    protected IPage Page { get; private set; } = default!;

    protected const string BaseUrl = "http://localhost:5006";
    protected const string PreferredAgentId = "assistant";
    protected string AgentId { get; private set; } = PreferredAgentId;

    public async Task InitializeAsync()
    {
        // Install browsers on first run (no-op if already installed)
        Microsoft.Playwright.Program.Main(["install", "chromium"]);

        // Skip all tests if gateway not running
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var http = new HttpClient();
            var r = await http.GetAsync($"{BaseUrl}/health", cts.Token);
            if (!r.IsSuccessStatusCode)
                throw new Exception($"Gateway returned {r.StatusCode}");
        }
        catch (Exception ex)
        {
            throw new SkipException($"Dev gateway not running at localhost:5006 — {ex.Message}");
        }

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new() 
        { 
            Headless = true,
            Args = ["--disable-cache", "--disk-cache-size=0"]
        });
        Context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true
        });
        Page = await Context.NewPageAsync();

        // Resolve agent ID: prefer "probe" but fall back to first available
        using var agentHttp = new HttpClient();
        var agentsJson = await agentHttp.GetStringAsync($"{BaseUrl}/api/agents");
        var agents = System.Text.Json.JsonSerializer.Deserialize<List<AgentInfo>>(agentsJson,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        if (!agents.Any(a => a.AgentId == PreferredAgentId) && agents.Count > 0)
            AgentId = agents[0].AgentId ?? PreferredAgentId;
    }

    public async Task DisposeAsync()
    {
        await Page.CloseAsync();
        await Context.CloseAsync();
        await Browser.CloseAsync();
        Playwright.Dispose();
    }

    /// <summary>
    /// Navigates to the portal root and waits until the Blazor app has fully rendered
    /// (loading spinner gone, main sidebar element present in the DOM).
    /// </summary>
    protected async Task WaitForPortalReadyAsync()
    {
        await Page.GotoAsync(BaseUrl);

        // Wait for sidebar element to be attached (may be CSS-hidden when closed)
        await Page.WaitForSelectorAsync(".main-sidebar", new() { Timeout = 15000, State = WaitForSelectorState.Attached });
    }

    /// <summary>
    /// Ensures the sidebar is open (toggles it if currently closed).
    /// </summary>
    protected async Task EnsureSidebarOpenAsync()
    {
        var sidebar = Page.Locator(".main-sidebar");
        var isClosed = await sidebar.EvaluateAsync<bool>("el => el.classList.contains('sidebar-closed')");
        if (isClosed)
        {
            await Page.Locator(".burger-btn").ClickAsync();
            // Wait for sidebar to open
            await Page.WaitForFunctionAsync(
                "() => !document.querySelector('.main-sidebar')?.classList.contains('sidebar-closed')",
                null,
                new() { Timeout = 5000 });
        }
    }

    /// <summary>
    /// Selects the specified agent from the dropdown and waits briefly for state to settle.
    /// </summary>
    protected async Task SelectAgentAsync(string agentId)
    {
        await EnsureSidebarOpenAsync();
        var dropdown = Page.Locator(".agent-dropdown-select");
        await dropdown.SelectOptionAsync(new SelectOptionValue { Value = agentId });
        await Page.WaitForTimeoutAsync(500);
    }
    /// <summary>
    /// Clicks the Default conversation (one with the default badge).
    /// Falls back to first available if no default badge found.
    /// </summary>
    protected async Task SelectDefaultConversationAsync()
    {
        var defaultConv = Page.Locator(".conversation-list-item:has(.conversation-default-badge)");
        if (await defaultConv.CountAsync() > 0)
            await defaultConv.First.ClickAsync();
        else
            await Page.Locator(".conversation-list-item").First.ClickAsync();
        await Page.WaitForTimeoutAsync(500);
    }

}
