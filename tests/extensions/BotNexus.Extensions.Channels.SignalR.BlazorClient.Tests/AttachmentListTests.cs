using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class AttachmentListTests : IDisposable
{
    private readonly BunitContext _context = new();

    public void Dispose() => _context.Dispose();

    [Fact]
    public void Renders_empty_state_without_entries()
    {
        var cut = _context.Render<AttachmentList>(parameters => parameters
            .Add(component => component.Attachments, []));

        cut.FindAll("[data-testid='message-attachment']").ShouldBeEmpty();
    }

    [Fact]
    public void Renders_download_and_bounded_image_entries()
    {
        var attachments = new[]
        {
            new ChatAttachment("notes.txt", "text/plain", 12, "SGVsbG8="),
            new ChatAttachment("photo.png", "image/png", 68, "iVBORw0KGgo=")
        };

        var cut = _context.Render<AttachmentList>(parameters => parameters
            .Add(component => component.Attachments, attachments));

        cut.FindAll("[data-testid='message-attachment']").Count.ShouldBe(2);
        var link = cut.Find("a[data-testid='attachment-download']");
        link.GetAttribute("href").ShouldBe(attachments[0].DataUrl);
        link.GetAttribute("download").ShouldBe("notes.txt");
        link.TextContent.ShouldContain("12 B");
        var image = cut.Find("img[data-testid='attachment-image']");
        image.ClassList.ShouldContain("attachment-thumbnail");
        image.GetAttribute("src").ShouldBe(attachments[1].DataUrl);
    }

    [Fact]
    public void Styles_bound_thumbnails_and_lightbox_to_the_viewport()
    {
        var css = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "wwwroot",
            "css",
            "AttachmentList.razor.css"));

        css.ShouldContain(".attachment-thumbnail");
        css.ShouldContain("max-width: min(100%, 28rem)");
        css.ShouldContain("max-height: 20rem");
        css.ShouldContain(".attachment-lightbox-image");
        css.ShouldContain("max-width: calc(100vw - 2rem)");
        css.ShouldContain("max-height: calc(100vh - 2rem)");
        css.ShouldContain("@media (max-width: 600px)");
    }

    [Fact]
    public void Image_lightbox_closes_from_backdrop_and_escape()
    {
        var attachment = new ChatAttachment("photo.png", "image/png", 68, "iVBORw0KGgo=");
        var cut = _context.Render<AttachmentList>(parameters => parameters
            .Add(component => component.Attachments, new[] { attachment }));

        cut.Find("button[data-testid='attachment-image-open']").Click();
        cut.Find("[data-testid='attachment-lightbox']");
        cut.Find("[data-testid='attachment-lightbox']").Click();
        cut.FindAll("[data-testid='attachment-lightbox']").ShouldBeEmpty();

        cut.Find("button[data-testid='attachment-image-open']").Click();
        cut.Find("[data-testid='attachment-lightbox']").KeyDown("Escape");
        cut.FindAll("[data-testid='attachment-lightbox']").ShouldBeEmpty();
    }
}
