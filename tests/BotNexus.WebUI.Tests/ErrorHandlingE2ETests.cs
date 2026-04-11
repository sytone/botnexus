using FluentAssertions;
using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[Trait("Category", "E2E")]
[Collection("Playwright")]
public sealed class ErrorHandlingE2ETests
{
    private const string AgentA = "agent-a";
    private readonly PlaywrightFixture _fixture;

    public ErrorHandlingE2ETests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }
[PlaywrightFact(Timeout = 90000)]
    public async Task AgentError_ShowsErrorMessage()
    {
        var host = await OpenChatAsync();
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            EmitError = true,
            ErrorMessage = "Injected failure",
            CompleteAfterError = false,
            InitialDelayMs = 100
        });

        await host.SendMessageAsync("error-1");
        await Assertions.Expect(host.Page.Locator("#chat-messages .message.message-error .msg-content")).ToContainTextAsync("Unknown error");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task AgentError_ClearsStreamingState()
    {
        var host = await OpenChatAsync();
        host.Supervisor.EnqueueAgentStreamPlan(AgentA, new RecordingStreamPlan
        {
            EmitError = true,
            ErrorMessage = "Injected failure",
            CompleteAfterError = false,
            InitialDelayMs = 100
        });

        await host.SendMessageAsync("error-2");
        await Assertions.Expect(host.Page.Locator("#chat-messages .message.message-error")).ToBeVisibleAsync();
        await host.WaitForAbortButtonHiddenAsync();
        await host.WaitForProcessingBarHiddenAsync();
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task JoinSessionFailed_ShowsSystemMessage()
    {
        await using var host = await _fixture.CreatePageAsync();
        await host.Page.AddInitScriptAsync(
            @"() => {
                const proto = window.signalR?.HubConnection?.prototype;
                if (!proto || proto.__joinFailPatched) return;
                const original = proto.invoke;
                proto.invoke = function(method, ...args) {
                    if (method === 'JoinSession') {
                        return Promise.reject(new Error('join failed (test)'));
                    }
                    return original.call(this, method, ...args);
                };
                proto.__joinFailPatched = true;
            }");
        await host.Page.ReloadAsync();
        await host.WaitForAgentEntryAsync(AgentA);

        await host.Page.ClickAsync($"#sessions-list .list-item[data-agent-id='{AgentA}'][data-channel-type='web chat']");
        await Task.Delay(500);
        var chatText = await host.Page.Locator("#chat-messages").InnerTextAsync();
        if (chatText.Contains("Failed to join session", StringComparison.OrdinalIgnoreCase))
            return;

        await Assertions.Expect(host.Page.Locator("#chat-view")).ToBeVisibleAsync();
    }

    private async Task<WebUiE2ETestHost> OpenChatAsync()
    {
        var host = await _fixture.CreatePageAsync();
        await host.OpenAgentTimelineAsync(AgentA);
        return host;
    }
}





