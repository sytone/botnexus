using System.Diagnostics;
using FluentAssertions;
using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[Trait("Category", "E2E")]
[Collection("Playwright")]
public sealed class SessionSwitchingE2ETests
{
    private const string AgentA = "agent-a";
    private const string AgentB = "agent-b";
    private readonly PlaywrightFixture _fixture;

    public SessionSwitchingE2ETests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task BasicSwitchAndSend_RoutesMessagesToSelectedSessions()
    {
        await using var host = await _fixture.CreatePageAsync();

        await host.OpenAgentTimelineAsync(AgentA);
        var sessionA = await host.SendMessageAsync("basic-a");

        await host.OpenAgentTimelineAsync(AgentB);
        var sessionB = await host.SendMessageAsync("basic-b");

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
        await using var host = await _fixture.CreatePageAsync();

        await host.OpenAgentTimelineAsync(AgentA);
        var sessionA = await host.SendMessageAsync("switchback-seed");

        await host.OpenAgentTimelineAsync(AgentB);
        await host.OpenAgentTimelineAsync(AgentA);

        await host.SendMessageAsync("switchback-target");

        var last = host.Supervisor.Dispatches.Last();
        last.AgentId.Should().Be(AgentA);
        last.SessionId.Should().Be(sessionA);
        last.Content.Should().Be("switchback-target");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task SendDuringLoading_DoesNotMisrouteToPreviousSession()
    {
        await using var host = await _fixture.CreatePageAsync();

        await host.OpenAgentTimelineAsync(AgentA);
        var sessionA = await host.SendMessageAsync("loading-seed-a");

        await host.OpenAgentTimelineAsync(AgentB);
        await host.SendMessageAsync("loading-seed-b");

        await host.OpenAgentTimelineAsync(AgentA);
        await host.Page.Locator($"#sessions-list .list-item[data-agent-id='{AgentB}'][data-channel-type='web chat']").First.ClickAsync();
        await Assertions.Expect(host.Page.Locator("#chat-title")).ToContainTextAsync(AgentB, new() { Timeout = 15000 });
        await Assertions.Expect(host.Page.Locator("#chat-input")).ToBeEditableAsync(new() { Timeout = 15000 });

        await host.SendMessageAsync("during-switch");

        host.Supervisor.Dispatches.Should().NotContain(record =>
            record.Content == "during-switch" &&
            record.AgentId == AgentA &&
            record.SessionId == sessionA);

        var last = host.Supervisor.Dispatches.Last();
        last.AgentId.Should().Be(AgentB);
        last.SessionId.Should().NotBe(sessionA);
        last.Content.Should().Be("during-switch");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task BackgroundSession_ReceivesEvents_WhileViewingOther()
    {
        await using var host = await _fixture.CreatePageAsync();

        await host.OpenAgentTimelineAsync(AgentA);
        var sessionA = await host.SendMessageAsync("background-seed-a");

        await host.OpenAgentTimelineAsync(AgentB);
        await host.SendMessageAsync("background-seed-b");

        var delayedPlan = new RecordingStreamPlan
        {
            InitialDelayMs = 300,
            DelayBetweenDeltasMs = 1200
        };
        delayedPlan.ContentDeltas.Add("echo:agent-a:background-event-1");
        delayedPlan.ContentDeltas.Add("echo:agent-a:background-event-2");
        host.Supervisor.EnqueueSessionStreamPlan(AgentA, sessionA, delayedPlan);

        await host.OpenAgentTimelineAsync(AgentA);
        await host.SendMessageAsync("background-trigger");
        await host.OpenAgentTimelineAsync(AgentB);

        var badge = host.Page.Locator($"#sessions-list .list-item[data-agent-id='{AgentA}'][data-channel-type='web chat'] .unread-badge");
        await Assertions.Expect(badge).ToBeVisibleAsync(new() { Timeout = 15000 });
        (await host.Page.Locator("#chat-messages").InnerTextAsync()).Should().NotContain("background-event-2");

        await host.OpenAgentTimelineAsync(AgentA);
        await Assertions.Expect(host.Page.Locator("#chat-messages")).ToContainTextAsync(
            "background-event-2",
            new() { Timeout = 15000 });
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task SidebarBadge_ShowsUnreadCount()
    {
        await using var host = await _fixture.CreatePageAsync();

        await host.OpenAgentTimelineAsync(AgentA);
        var sessionA = await host.SendMessageAsync("badge-seed-a");

        await host.OpenAgentTimelineAsync(AgentB);
        await host.SendMessageAsync("badge-seed-b");

        var delayedPlan = new RecordingStreamPlan { InitialDelayMs = 900 };
        delayedPlan.ContentDeltas.Add("echo:agent-a:badge-event");
        host.Supervisor.EnqueueSessionStreamPlan(AgentA, sessionA, delayedPlan);

        await host.OpenAgentTimelineAsync(AgentA);
        await host.SendMessageAsync("badge-trigger");
        await host.OpenAgentTimelineAsync(AgentB);

        var badge = host.Page.Locator($"#sessions-list .list-item[data-agent-id='{AgentA}'][data-channel-type='web chat'] .unread-badge");
        await Assertions.Expect(badge).ToBeVisibleAsync(new() { Timeout = 15000 });
        var badgeText = await badge.InnerTextAsync();
        badgeText.Should().NotBeNullOrWhiteSpace();
        badgeText.Should().NotBe("0");

        await host.OpenAgentTimelineAsync(AgentA);
        await Assertions.Expect(badge).ToHaveCountAsync(0, new() { Timeout = 15000 });
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task SubscribeAll_ReceivesAllSessions()
    {
        await using var host = await _fixture.CreatePageAsync();

        await host.WaitForConsoleMessageAsync("SubscribeAll: 2 sessions");
        host.GetHubInvocationCount("SubscribeAll").Should().BeGreaterThanOrEqualTo(1);
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task InstantSwitch_NoServerCall()
    {
        await using var host = await _fixture.CreatePageAsync();

        await host.OpenAgentTimelineAsync(AgentA);
        await host.SendMessageAsync("instant-seed-a");
        await host.OpenAgentTimelineAsync(AgentB);
        await host.SendMessageAsync("instant-seed-b");

        var subscribeAllBefore = host.GetHubInvocationCount("SubscribeAll");
        var joinBefore = host.GetHubInvocationCount("JoinSession");
        var leaveBefore = host.GetHubInvocationCount("LeaveSession");
        var sendBefore = host.GetHubInvocationCount("SendMessage");

        var sw = Stopwatch.StartNew();
        await host.OpenAgentTimelineAsync(AgentA);
        await host.OpenAgentTimelineAsync(AgentB);
        await host.OpenAgentTimelineAsync(AgentA);
        sw.Stop();

        host.GetHubInvocationCount("JoinSession").Should().Be(joinBefore);
        host.GetHubInvocationCount("LeaveSession").Should().Be(leaveBefore);
        host.GetHubInvocationCount("SubscribeAll").Should().Be(subscribeAllBefore);
        host.GetHubInvocationCount("SendMessage").Should().Be(sendBefore);
        sw.ElapsedMilliseconds.Should().BeLessThan(2500);
    }
}
