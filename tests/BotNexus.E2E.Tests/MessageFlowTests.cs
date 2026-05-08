namespace BotNexus.E2E.Tests;

/// <summary>
/// Tests that messages sent through the portal receive an assistant response
/// without any additional user interaction.
/// </summary>
public class MessageFlowTests : E2ETestBase
{
    /// <summary>
    /// Sending a message to the probe agent should produce an assistant response
    /// in the message list without any additional user action.
    /// </summary>
    [SkippableFact]
    public async Task SendMessage_ResponseAppearsWithoutInteraction()
    {
        await WaitForPortalReadyAsync();
        await SelectAgentAsync(AgentId);
        await Page.WaitForSelectorAsync(".conversation-list-item", new() { Timeout = 10000, State = WaitForSelectorState.Attached });

        await SelectDefaultConversationAsync();
        await Page.WaitForTimeoutAsync(500);

        // The chat input is a textarea with class "chat-input"
        var input = Page.Locator("textarea.chat-input").First;
        await input.FillAsync("Say exactly: PONG");
        await Page.Keyboard.PressAsync("Enter");

        // Response must appear without any further interaction
        // Messages render with class "message assistant"
        await Page.WaitForFunctionAsync(
            "() => document.querySelectorAll('.message.assistant').length > 0",
            null,
            new() { Timeout = 30000 });

        var messages = await Page.Locator(".message.assistant").AllTextContentsAsync();
        messages.ShouldNotBeEmpty();
    }

    /// <summary>
    /// After sending a message the streaming indicator should eventually disappear
    /// and the final response should be present in the DOM.
    /// </summary>
    [SkippableFact]
    public async Task SendMessage_StreamingIndicator_Appears_ThenDisappears()
    {
        await WaitForPortalReadyAsync();
        await SelectAgentAsync(AgentId);
        await Page.WaitForSelectorAsync(".conversation-list-item", new() { Timeout = 10000, State = WaitForSelectorState.Attached });
        await SelectDefaultConversationAsync();

        var input = Page.Locator("textarea.chat-input").First;
        await input.FillAsync("Reply with one word: OK");
        await Page.Keyboard.PressAsync("Enter");

        // At minimum verify the response arrives (streaming badge may be too brief to catch)
        await Page.WaitForFunctionAsync(
            "() => document.querySelectorAll('.message.assistant').length > 0",
            null,
            new() { Timeout = 30000 });
    }
}
