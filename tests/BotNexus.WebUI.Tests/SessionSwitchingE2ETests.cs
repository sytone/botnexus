using FluentAssertions;
using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[Trait("Category", "E2E")]
public sealed class SessionSwitchingE2ETests : IAsyncLifetime
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
    public async Task BasicSwitchAndSend_RoutesMessagesToSelectedSessions()
    {
        var host = GetHost();

        await host.OpenAgentTimelineAsync(AgentA);
        var sessionA = await host.SendMessageAsync("basic-a");
        await host.WaitForInvocationCountAsync(1);

        await host.OpenAgentTimelineAsync(AgentB);
        var sessionB = await host.SendMessageAsync("basic-b");
        await host.WaitForInvocationCountAsync(2);

        var first = host.Supervisor.Dispatches[0];
        var second = host.Supervisor.Dispatches[1];

        first.AgentId.Should().Be(AgentA);
        first.SessionId.Should().Be(sessionA);
        first.Content.Should().Be("basic-a");

        second.AgentId.Should().Be(AgentB);
        second.SessionId.Should().Be(sessionB);
        second.Content.Should().Be("basic-b");
        sessionB.Should().NotBe(sessionA);
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task SwitchBackAndSend_RoutesToOriginalSession()
    {
        var host = GetHost();

        await host.OpenAgentTimelineAsync(AgentA);
        var sessionA = await host.SendMessageAsync("switchback-seed");
        await host.WaitForInvocationCountAsync(1);

        await host.OpenAgentTimelineAsync(AgentB);
        await host.OpenAgentTimelineAsync(AgentA);

        await host.SendMessageAsync("switchback-target");
        await host.WaitForInvocationCountAsync(2);

        var last = host.Supervisor.Dispatches.Last();
        last.AgentId.Should().Be(AgentA);
        last.SessionId.Should().Be(sessionA);
        last.Content.Should().Be("switchback-target");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task RapidSwitchAndSend_RoutesToLatestSelection()
    {
        var host = GetHost();

        await host.OpenAgentTimelineAsync(AgentA);
        await host.SendMessageAsync("rapid-seed-a");
        await host.WaitForInvocationCountAsync(1);

        await host.OpenAgentTimelineAsync(AgentB);
        var sessionB = await host.SendMessageAsync("rapid-seed-b");
        await host.WaitForInvocationCountAsync(2);

        await host.Page.Locator($"#sessions-list .list-item[data-agent-id='{AgentA}'][data-channel-type='web chat']").First.ClickAsync();
        await host.Page.Locator($"#sessions-list .list-item[data-agent-id='{AgentB}'][data-channel-type='web chat']").First.ClickAsync();
        await Assertions.Expect(host.Page.Locator("#chat-title")).ToContainTextAsync(AgentB, new() { Timeout = 15000 });

        await host.SendMessageAsync("rapid-target");
        await host.WaitForInvocationCountAsync(3);

        var last = host.Supervisor.Dispatches.Last();
        last.AgentId.Should().Be(AgentB);
        last.SessionId.Should().Be(sessionB);
        last.Content.Should().Be("rapid-target");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task SendDuringLoading_DoesNotMisrouteToPreviousSession()
    {
        var host = GetHost();

        await host.OpenAgentTimelineAsync(AgentA);
        var sessionA = await host.SendMessageAsync("loading-seed-a");
        await host.WaitForInvocationCountAsync(1);

        await host.OpenAgentTimelineAsync(AgentB);
        var sessionB = await host.SendMessageAsync("loading-seed-b");
        await host.WaitForInvocationCountAsync(2);

        await host.OpenAgentTimelineAsync(AgentA);

        await host.Page.Locator($"#sessions-list .list-item[data-agent-id='{AgentB}'][data-channel-type='web chat']").First.ClickAsync();
        await Assertions.Expect(host.Page.Locator("#chat-input")).ToBeDisabledAsync();

        await host.Page.EvaluateAsync(
            "(message) => { const input = document.querySelector('#chat-input'); const send = document.querySelector('#btn-send'); input.value = message; input.dispatchEvent(new Event('input')); send.click(); }",
            "during-loading");

        await Task.Delay(250);

        await host.SendMessageAsync("after-loading");
        await host.WaitForInvocationCountAsync(3);

        host.Supervisor.Dispatches.Should().NotContain(record =>
            record.Content == "during-loading" &&
            record.AgentId == AgentA &&
            record.SessionId == sessionA);

        var last = host.Supervisor.Dispatches.Last();
        last.AgentId.Should().Be(AgentB);
        last.SessionId.Should().Be(sessionB);
        last.Content.Should().Be("after-loading");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task InboundEvents_AreIsolatedToOriginSession()
    {
        var host = GetHost();

        await host.OpenAgentTimelineAsync(AgentA);
        var sessionA = await host.SendMessageAsync("isolation-seed-a");
        await host.WaitForInvocationCountAsync(1);

        await host.OpenAgentTimelineAsync(AgentB);
        await host.SendMessageAsync("isolation-seed-b");
        await host.WaitForInvocationCountAsync(2);

        await host.OpenAgentTimelineAsync(AgentA);
        await host.SendMessageAsync("delayed-isolation");
        await host.WaitForInvocationCountAsync(3);

        await host.Page.Locator($"#sessions-list .list-item[data-agent-id='{AgentB}'][data-channel-type='web chat']").First.ClickAsync();
        await Assertions.Expect(host.Page.Locator("#chat-title")).ToContainTextAsync(AgentB, new() { Timeout = 15000 });

        await Task.Delay(1200);

        var delayedResponse = "echo:agent-a:delayed-isolation";
        (await host.Page.Locator("#chat-messages").InnerTextAsync()).Should().NotContain(delayedResponse);

        await host.OpenAgentTimelineAsync(AgentA);
        (await host.Page.Locator("#chat-messages").InnerTextAsync()).Should().Contain(delayedResponse);
        (await host.WaitForCurrentSessionIdAsync()).Should().Be(sessionA);
    }

    private WebUiE2ETestHost GetHost()
        => _host ?? throw new InvalidOperationException("Playwright host was not initialized.");
}
