using FluentAssertions;
using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[Trait("Category", "E2E")]
[Collection("Playwright")]
public sealed class SidebarNavigationE2ETests
{
    private const string AgentA = "agent-a";
    private const string AgentB = "agent-b";
    private readonly PlaywrightFixture _fixture;

    public SidebarNavigationE2ETests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }
[PlaywrightFact(Timeout = 90000)]
    public async Task ChannelEntry_OpensTimeline()
    {
        await using var host = await _fixture.CreatePageAsync();
        await host.Page.Locator($"#sessions-list .list-item[data-agent-id='{AgentA}'][data-channel-type='web chat']").First.ClickAsync();
        await Assertions.Expect(host.Page.Locator("#chat-view")).ToBeVisibleAsync();
        await Assertions.Expect(host.Page.Locator("#chat-title")).ToContainTextAsync(AgentA);
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task AgentGroupHeader_TogglesCollapse()
    {
        await using var host = await _fixture.CreatePageAsync();
        var header = host.Page.Locator("#sessions-list .agent-group-header").First;
        await header.ClickAsync();
        (await header.GetAttributeAsync("class")).Should().Contain("collapsed");
        await header.ClickAsync();
        (await header.GetAttributeAsync("class")).Should().NotContain("collapsed");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task ActiveChannel_HighlightedInSidebar()
    {
        await using var host = await _fixture.CreatePageAsync();
        await host.OpenAgentTimelineAsync(AgentB);
        var activeEntry = host.Page.Locator($"#sessions-list .list-item.active[data-agent-id='{AgentB}'][data-channel-type='web chat']").First;
        await Assertions.Expect(activeEntry).ToBeVisibleAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task RefreshSessions_ReloadsFromAPI()
    {
        await using var host = await _fixture.CreatePageAsync();
        await host.OpenAgentTimelineAsync(AgentA);
        var sessionId = await host.SendMessageAsync("refresh-seed");
        await host.WaitForStreamingCompleteAsync();
        await host.Page.ClickAsync("#btn-refresh-sessions");
        await host.Page.Locator($"#sessions-list .list-item[data-session-id='{sessionId}']").WaitForAsync(new() { Timeout = 15000 });
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task SectionHeaders_ToggleCollapse()
    {
        await using var host = await _fixture.CreatePageAsync();
        var header = host.Page.Locator(".section-header[data-toggle='channels-list']").First;
        var section = host.Page.Locator("#channels-list");
        await header.ClickAsync();
        (await section.GetAttributeAsync("class")).Should().Contain("collapsed");
        await header.ClickAsync();
        (await section.GetAttributeAsync("class")).Should().NotContain("collapsed");
    }
}





