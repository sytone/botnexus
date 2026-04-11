using FluentAssertions;
using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[Trait("Category", "E2E")]
[Collection("Playwright")]
public sealed class ConnectionLifecycleE2ETests
{
    private const string AgentA = "agent-a";
    private readonly PlaywrightFixture _fixture;

    public ConnectionLifecycleE2ETests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }
[PlaywrightFact(Timeout = 90000)]
    public async Task InitialLoad_ShowsConnectedStatus()
    {
        await using var host = await _fixture.CreatePageAsync();
        await Assertions.Expect(host.Page.Locator("#connection-status.connected")).ToBeVisibleAsync();
        await Assertions.Expect(host.Page.Locator("#connection-status .status-text")).ToContainTextAsync("Connected");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task InitialLoad_LoadsAgentsInSidebar()
    {
        await using var host = await _fixture.CreatePageAsync();
        await Assertions.Expect(host.Page.Locator("#agents-list .list-item")).ToHaveCountAsync(2, new() { Timeout = 15000 });
        var text = await host.Page.Locator("#agents-list").InnerTextAsync();
        text.Should().Contain(AgentA);
        text.Should().Contain("agent-b");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task InitialLoad_LoadsSessionsInSidebar()
    {
        await using var host = await _fixture.CreatePageAsync();
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
        await using var host = await _fixture.CreatePageAsync();
        await Assertions.Expect(host.Page.Locator("#welcome-screen")).ToBeVisibleAsync();
        await Assertions.Expect(host.Page.Locator("#chat-view")).ToBeHiddenAsync();
    }
}





