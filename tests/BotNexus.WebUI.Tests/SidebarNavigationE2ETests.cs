using FluentAssertions;
using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[Trait("Category", "E2E")]
public sealed class SidebarNavigationE2ETests : IAsyncLifetime
{
    private const string AgentA = "agent-a";
    private const string AgentB = "agent-b";
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
    public async Task ChannelEntry_OpensTimeline()
    {
        var host = GetHost();
        await host.Page.Locator($"#sessions-list .list-item[data-agent-id='{AgentA}'][data-channel-type='web chat']").First.ClickAsync();
        await Assertions.Expect(host.Page.Locator("#chat-view")).ToBeVisibleAsync();
        await Assertions.Expect(host.Page.Locator("#chat-title")).ToContainTextAsync(AgentA);
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task AgentGroupHeader_TogglesCollapse()
    {
        var host = GetHost();
        var header = host.Page.Locator("#sessions-list .agent-group-header").First;
        await header.ClickAsync();
        (await header.GetAttributeAsync("class")).Should().Contain("collapsed");
        await header.ClickAsync();
        (await header.GetAttributeAsync("class")).Should().NotContain("collapsed");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task ActiveChannel_HighlightedInSidebar()
    {
        var host = GetHost();
        await host.OpenAgentTimelineAsync(AgentB);
        var activeEntry = host.Page.Locator($"#sessions-list .list-item.active[data-agent-id='{AgentB}'][data-channel-type='web chat']").First;
        await Assertions.Expect(activeEntry).ToBeVisibleAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task RefreshSessions_ReloadsFromAPI()
    {
        var host = GetHost();
        await host.OpenAgentTimelineAsync(AgentA);
        var sessionId = await host.SendMessageAsync("refresh-seed");
        await host.WaitForStreamingCompleteAsync();
        await host.Page.ClickAsync("#btn-refresh-sessions");
        await host.Page.Locator($"#sessions-list .list-item[data-session-id='{sessionId}']").WaitForAsync(new() { Timeout = 15000 });
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task SectionHeaders_ToggleCollapse()
    {
        var host = GetHost();
        var header = host.Page.Locator(".section-header[data-toggle='channels-list']").First;
        var section = host.Page.Locator("#channels-list");
        await header.ClickAsync();
        (await section.GetAttributeAsync("class")).Should().Contain("collapsed");
        await header.ClickAsync();
        (await section.GetAttributeAsync("class")).Should().NotContain("collapsed");
    }

    private WebUiE2ETestHost GetHost()
        => _host ?? throw new InvalidOperationException("Playwright host was not initialized.");
}
