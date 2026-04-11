using FluentAssertions;
using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[Trait("Category", "E2E")]
public sealed class ConnectionLifecycleE2ETests : IAsyncLifetime
{
    private const string AgentA = "agent-a";
    private WebUiE2ETestHost? _host;

    public async Task InitializeAsync()
    {
        _host = await WebUiE2ETestHost.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task InitialLoad_ShowsConnectedStatus()
    {
        var host = GetHost();
        await Assertions.Expect(host.Page.Locator("#connection-status.connected")).ToBeVisibleAsync();
        await Assertions.Expect(host.Page.Locator("#connection-status .status-text")).ToContainTextAsync("Connected");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task InitialLoad_LoadsAgentsInSidebar()
    {
        var host = GetHost();
        await Assertions.Expect(host.Page.Locator("#agents-list .list-item")).ToHaveCountAsync(2, new() { Timeout = 15000 });
        var text = await host.Page.Locator("#agents-list").InnerTextAsync();
        text.Should().Contain(AgentA);
        text.Should().Contain("agent-b");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task InitialLoad_LoadsSessionsInSidebar()
    {
        var host = GetHost();
        await host.OpenAgentTimelineAsync(AgentA);
        var sessionId = await host.SendMessageAsync("seed-session-list");
        await host.WaitForStreamingCompleteAsync();
        await host.Page.ClickAsync("#btn-refresh-sessions");
        await host.Page.Locator($"#sessions-list .list-item[data-agent-id='{AgentA}'][data-session-id='{sessionId}']").WaitForAsync(
            new LocatorWaitForOptions { Timeout = 15000 });
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task InitialLoad_ShowsWelcomeScreen()
    {
        var host = GetHost();
        await Assertions.Expect(host.Page.Locator("#welcome-screen")).ToBeVisibleAsync();
        await Assertions.Expect(host.Page.Locator("#chat-view")).ToBeHiddenAsync();
    }

    private WebUiE2ETestHost GetHost()
        => _host ?? throw new InvalidOperationException("Playwright host was not initialized.");
}
