using Microsoft.Playwright;
using BotNexus.Integration.E2E.Tests.PageObjects;

namespace BotNexus.Integration.E2E.Tests;

[Collection(NewUserExperienceCollection.Name)]
public sealed class ClipboardImagePasteTests
{
    private readonly NewUserExperienceFixture _fixture;

    public ClipboardImagePasteTests(NewUserExperienceFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Active_composer_real_image_paste_stages_non_empty_attachment_after_replacement_and_reopen()
    {
        Skip.IfNot(_fixture.Succeeded, $"Fixture failed: {_fixture.Error}");

        using var playwright = await Playwright.CreateAsync();
        var (browser, skipReason) = await PortalTestHelpers.TryLaunchBrowserAsync(playwright);
        Skip.If(browser is null, skipReason);

        await using var _ = browser!;
        var (page, _, chat) = await PortalTestHelpers.NewChatPageAsync(
            browser, _fixture.GatewayBaseUrl, _fixture.AgentIds[0]);

        await chat.StartFreshSessionAsync();
        await AssertImagePasteStagesAttachmentAsync(page, chat.ChatInput, chat.Root.Locator("[data-testid='attachment-chip']"));

        await chat.Root.Locator("[data-testid='attachment-remove']").ClickAsync();
        await chat.SendMessageAsync("ASK_USER_FREEFORM");
        var prompt = chat.Root.Locator(".ask-user-prompt");
        await prompt.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 35_000 });
        await prompt.Locator(".cancel-btn").ClickAsync();
        await chat.ChatInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await AssertImagePasteStagesAttachmentAsync(page, chat.ChatInput, chat.Root.Locator("[data-testid='attachment-chip']"));

        await chat.Root.Locator("[data-testid='attachment-remove']").ClickAsync();
        await chat.Root.Locator("[data-testid='chat-expand']").ClickAsync();
        var expandedInput = page.Locator("[data-testid='expanded-composer-input']");
        await AssertImagePasteStagesAttachmentAsync(page, expandedInput, page.Locator("[data-testid='expanded-attachment-chip']"));
        await page.Locator("[data-testid='expanded-attachment-remove']").ClickAsync();
        await page.Locator("[data-testid='expanded-composer-close']").ClickAsync();
        await chat.Root.Locator("[data-testid='chat-expand']").ClickAsync();
        await AssertImagePasteStagesAttachmentAsync(page, expandedInput, page.Locator("[data-testid='expanded-attachment-chip']"));
    }

    private static async Task AssertImagePasteStagesAttachmentAsync(IPage page, ILocator input, ILocator chip)
    {
        await input.FillAsync("draft text remains");
        await input.EvaluateAsync("""
            element => {
                const bytes = new Uint8Array([137, 80, 78, 71]);
                const file = new File([bytes], 'clipboard.png', { type: 'image/png' });
                const transfer = new DataTransfer();
                transfer.items.add(file);
                element.dispatchEvent(new ClipboardEvent('paste', {
                    bubbles: true,
                    cancelable: true,
                    clipboardData: transfer
                }));
            }
            """);

        await chip.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        await page.WaitForFunctionAsync(
            "element => element && element.textContent.includes('clipboard.png')",
            await chip.ElementHandleAsync());
        Assert.Equal("draft text remains", await input.InputValueAsync());
    }
}
