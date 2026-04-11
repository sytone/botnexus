using FluentAssertions;
using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[Trait("Category", "E2E")]
[Collection("Playwright")]
public sealed class ChatSendingE2ETests
{
    private const string AgentA = "agent-a";
    private const string AgentB = "agent-b";
    private readonly PlaywrightFixture _fixture;

    public ChatSendingE2ETests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }
[PlaywrightFact(Timeout = 90000)]
    public async Task SendButton_DisabledWhenEmpty()
    {
        var host = await OpenChatAsync();
        await host.Page.FillAsync("#chat-input", string.Empty);
        await Assertions.Expect(host.Page.Locator("#btn-send")).ToBeDisabledAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task SendButton_EnabledWhenTextEntered()
    {
        var host = await OpenChatAsync();
        await host.Page.FillAsync("#chat-input", "hello");
        await Assertions.Expect(host.Page.Locator("#btn-send")).ToBeEnabledAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task EnterKey_SendsMessage()
    {
        var host = await OpenChatAsync();
        await host.Page.FillAsync("#chat-input", "enter-send");
        await host.Page.PressAsync("#chat-input", "Enter");
        await host.WaitForInvocationCountAsync(1);
        host.Supervisor.Dispatches.Count(d => d.Kind == DispatchKind.Send).Should().Be(1);
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task ShiftEnter_InsertsNewline()
    {
        var host = await OpenChatAsync();
        await host.Page.FillAsync("#chat-input", "line1");
        await host.Page.PressAsync("#chat-input", "Shift+Enter");
        await host.Page.TypeAsync("#chat-input", "line2");
        await Task.Delay(200);

        host.Supervisor.Dispatches.Count(d => d.Kind == DispatchKind.Send).Should().Be(0);
        (await host.Page.InputValueAsync("#chat-input")).Should().Contain("\n").And.Contain("line2");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task SendMessage_AppendsUserBubble()
    {
        var host = await OpenChatAsync();
        await host.SendMessageAsync("bubble-check");
        await Assertions.Expect(host.Page.Locator("#chat-messages .message.user")).ToContainTextAsync("bubble-check");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task SendMessage_ClearsInput()
    {
        var host = await OpenChatAsync();
        await host.Page.FillAsync("#chat-input", "clear-me");
        await host.Page.ClickAsync("#btn-send");
        await host.WaitForInvocationCountAsync(1);
        (await host.Page.InputValueAsync("#chat-input")).Should().BeEmpty();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task SendMessage_ReceivesStreamedResponse()
    {
        var host = await OpenChatAsync();
        await host.SendMessageAsync("stream-me");
        await host.WaitForStreamingCompleteAsync();
        await Assertions.Expect(host.Page.Locator("#chat-messages .message.assistant .msg-content")).ToContainTextAsync("echo:agent-a:stream-me");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task SendButton_DisabledDuringSessionSwitch()
    {
        var host = await OpenChatAsync();
        await host.SendMessageAsync("switch-seed-a");
        await host.WaitForStreamingCompleteAsync();
        await host.OpenAgentTimelineAsync(AgentB);
        await host.SendMessageAsync("switch-seed-b");
        await host.WaitForStreamingCompleteAsync();

        await host.Page.Locator($"#sessions-list .list-item[data-agent-id='{AgentA}'][data-channel-type='web chat']").First.ClickAsync();
        await Assertions.Expect(host.Page.Locator("#chat-input")).ToBeDisabledAsync(new() { Timeout = 5000 });
        await Assertions.Expect(host.Page.Locator("#btn-send")).ToBeDisabledAsync(new() { Timeout = 5000 });
    }

    private async Task<WebUiE2ETestHost> OpenChatAsync()
    {
        var host = await _fixture.CreatePageAsync();
        await host.OpenAgentTimelineAsync(AgentA);
        return host;
    }
}





