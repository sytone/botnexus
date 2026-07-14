namespace BotNexus.E2E.Tests;

/// <summary>
/// Tests that streaming state does not bleed between conversations.
/// Regression test for: switching conversations while agent is streaming
/// caused the Send button to flicker to "Steer" in the inactive conversation.
/// </summary>
public sealed class StreamingIsolationTests : E2ETestBase
{
    [SkippableFact]
    public async Task SwitchConversation_WhileStreaming_SendButtonDoesNotBecomeSteer()
    {
        // This test verifies that IsStreaming state is per-conversation,
        // not per-agent. Switching to a different conversation while the
        // agent is streaming in another should show "Send", not "Steer".

        await WaitForPortalReadyAsync();
        await SelectAgentAsync(AgentId);
        await Page.WaitForSelectorAsync(".conversation-list-item",
            new() { Timeout = 20000, State = WaitForSelectorState.Attached });

        // Ensure at least two conversations exist
        var convCount = await Page.Locator(".conversation-list-item").CountAsync();
        if (convCount < 2)
        {
            await Page.Locator(".conversation-new-btn").ClickAsync();
            await Page.WaitForTimeoutAsync(1000);
        }

        // Select the first (default) conversation
        await SelectDefaultConversationAsync();
        await Page.WaitForTimeoutAsync(500);

        // Send a message that will produce a long streaming response
        var input = Page.Locator("textarea").First;
        await input.FillAsync("Count from 1 to 20, one number per line.");
        await Page.Keyboard.PressAsync("Enter");

        // While streaming starts, immediately switch to the second conversation
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('.steer-btn, .streaming-badge') !== null",
            null, new() { Timeout = 10000 });

        // Switch to another conversation
        var otherConv = Page.Locator(".conversation-list-item:not(.active)").First;
        await otherConv.ClickAsync();
        await Page.WaitForTimeoutAsync(500);

        // In the OTHER conversation, Send button must NOT show "Steer"
        // This was the bug: streaming state bled across conversations
        var steerBtn = Page.Locator(".steer-btn");
        var steerCount = await steerBtn.CountAsync();
        steerCount.ShouldBe(0,
            "Steer button must not appear in a conversation that is not streaming");

        // The Send button must be present and enabled
        var sendBtn = Page.Locator(".send-btn");
        (await sendBtn.CountAsync()).ShouldBeGreaterThan(0,
            "Send button must be visible when the active conversation is not streaming");

        var sendDisabled = await sendBtn.First.IsDisabledAsync();
        sendDisabled.ShouldBeFalse(
            "Send button must not be disabled in a non-streaming conversation");
    }

    [SkippableFact]
    public async Task ActiveConversation_WhileStreaming_ShowsSteerNotSend()
    {
        // Verify the CORRECT state: the actively streaming conversation shows Steer

        await WaitForPortalReadyAsync();
        await SelectAgentAsync(AgentId);
        await Page.WaitForSelectorAsync(".conversation-list-item",
            new() { Timeout = 20000, State = WaitForSelectorState.Attached });

        await SelectDefaultConversationAsync();
        await Page.WaitForTimeoutAsync(500);

        var input = Page.Locator("textarea").First;
        await input.FillAsync("Count from 1 to 10, one number per line.");
        await Page.Keyboard.PressAsync("Enter");

        // While streaming, this conversation SHOULD show Steer
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('.steer-btn') !== null || document.querySelector('.streaming-badge') !== null",
            null, new() { Timeout = 15000 });

        // Either steer button or streaming badge confirms streaming is visible
        var streamingVisible =
            (await Page.Locator(".steer-btn").CountAsync()) > 0 ||
            (await Page.Locator(".streaming-badge").CountAsync()) > 0;

        streamingVisible.ShouldBeTrue(
            "Actively streaming conversation should show streaming state");
    }
}
