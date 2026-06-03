using Microsoft.Playwright;
using BotNexus.Integration.E2E.Tests.PageObjects;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Regression tests ensuring the mic/audio recording button is absent from the UI.
///
/// Background: Audio recording via the web microphone API was never fully
/// implemented. The mic button was visible in the chat input area but clicking
/// it would call a non-existent JS function, producing a silent JS error and
/// confusing users. The button (and its active-recording variant) have been
/// removed until the feature is properly built.
///
/// These tests prevent the button from silently re-appearing in a future merge.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class MicButtonRemovedTests
{
    private readonly NewUserExperienceFixture _fx;

    public MicButtonRemovedTests(NewUserExperienceFixture fx) => _fx = fx;

    [SkippableFact]
    [Trait("Category", "Regression")]
    [Trait("Issue", "mic-button-removed")]
    public async Task MicButton_IsNotPresent_InChatInputArea()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        // The mic button CSS class must not exist anywhere in the DOM
        await Assertions.Expect(page.Locator(".mic-btn")).ToHaveCountAsync(0);
    }

    [SkippableFact]
    [Trait("Category", "Regression")]
    [Trait("Issue", "mic-button-removed")]
    public async Task RecordingButton_IsNotPresent_InChatInputArea()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        // The active-recording stop button must also be absent
        await Assertions.Expect(page.Locator(".recording-btn")).ToHaveCountAsync(0);
    }

    [SkippableFact]
    [Trait("Category", "Regression")]
    [Trait("Issue", "mic-button-removed")]
    public async Task NoButton_HasMicOrRecordingTitle_InChatArea()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (page, _, _) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        // Neither tooltip should be present
        await Assertions.Expect(page.Locator("[title='Record audio message']")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator("[title='Stop recording']")).ToHaveCountAsync(0);
    }

    [SkippableFact]
    [Trait("Category", "Regression")]
    [Trait("Issue", "mic-button-removed")]
    public async Task ChatInputArea_SendButton_IsStillPresent_AfterMicRemoval()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture failed: {_fx.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (_, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fx.GatewayBaseUrl, _fx.AgentIds[0]);

        // Verify the send button is still intact — the mic removal must not
        // have disturbed the chat input layout
        await Assertions.Expect(chat.SendBtn).ToBeVisibleAsync();
    }
}
