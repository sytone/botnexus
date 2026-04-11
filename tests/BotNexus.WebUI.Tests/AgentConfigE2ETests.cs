using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[Trait("Category", "E2E")]
[Collection("Playwright")]
public sealed class AgentConfigE2ETests
{
    private const string AgentA = "agent-a";
    private readonly PlaywrightFixture _fixture;

    public AgentConfigE2ETests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }
[PlaywrightFact(Timeout = 90000)]
    public async Task ClickAgent_OpensConfigView()
    {
        await using var host = await _fixture.CreatePageAsync();
        await host.Page.ClickAsync($"#agents-list .list-item:has-text('{AgentA}')");
        await Assertions.Expect(host.Page.Locator("#agent-config-view")).ToBeVisibleAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task SaveConfig_PutsUpdatedAgent()
    {
        await using var host = await _fixture.CreatePageAsync();
        await host.Page.ClickAsync($"#agents-list .list-item:has-text('{AgentA}')");

        const string displayName = "Agent A Updated";
        await host.Page.FillAsync("#cfg-displayName", displayName);
        await host.Page.ClickAsync("#btn-agent-save");
        await Assertions.Expect(host.Page.Locator("#chat-messages .message.system-msg")).ToContainTextAsync("Agent settings saved.");

        var agent = await host.ApiClient.GetFromJsonAsync<JsonElement>($"/api/agents/{AgentA}");
        agent.GetProperty("displayName").GetString().Should().Be(displayName);
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task OpenChat_SwitchesToChatView()
    {
        await using var host = await _fixture.CreatePageAsync();
        await host.Page.ClickAsync($"#agents-list .list-item:has-text('{AgentA}')");
        await host.Page.ClickAsync("#btn-agent-chat");
        await Assertions.Expect(host.Page.Locator("#chat-view")).ToBeVisibleAsync();
        await Assertions.Expect(host.Page.Locator("#chat-title")).ToContainTextAsync(AgentA);
    }
}



