using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[Trait("Category", "E2E")]
[Collection("Playwright")]
public sealed class ModalDialogE2ETests
{
    private readonly PlaywrightFixture _fixture;

    public ModalDialogE2ETests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }
[PlaywrightFact(Timeout = 90000)]
    public async Task ConfirmDialog_OkExecutesCallback()
    {
        await using var host = await _fixture.CreatePageAsync();
        await host.Page.ClickAsync("#btn-stop-gateway");
        await Assertions.Expect(host.Page.Locator("#confirm-dialog")).ToBeVisibleAsync();
        await host.Page.ClickAsync("#btn-confirm-ok");
        await Assertions.Expect(host.Page.Locator("#chat-messages .message.system-msg")).ToContainTextAsync("Gateway restart initiated.");
    }

    [PlaywrightFact(Timeout = 90000)]
    public async Task ConfirmDialog_CancelDismisses()
    {
        await using var host = await _fixture.CreatePageAsync();
        await host.Page.ClickAsync("#btn-stop-gateway");
        await Assertions.Expect(host.Page.Locator("#confirm-dialog")).ToBeVisibleAsync();
        await host.Page.ClickAsync("#btn-confirm-cancel");
        await Assertions.Expect(host.Page.Locator("#confirm-dialog")).ToBeHiddenAsync();
    }
}





