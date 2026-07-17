using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class AttachmentDraftTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly IAgentInteractionService _interaction = Substitute.For<IAgentInteractionService>();

    public AttachmentDraftTests()
    {
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(new SlashCommandDispatcher(_interaction));
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _store.UpsertAgent(new AgentState { AgentId = "agent-1", DisplayName = "Agent", IsConnected = true });
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Default_render_shows_accessible_attachment_picker_without_drafts()
    {
        var cut = Render();
        cut.Find("[data-testid='chat-attach']");
        cut.Find("input[type='file'][multiple]");
        cut.FindAll("[data-testid='attachment-chip']").ShouldBeEmpty();
    }

    [Fact]
    public async Task Generic_selection_and_remove_manage_multiple_drafts()
    {
        var cut = Render();
        await cut.InvokeAsync(() => cut.Instance.AddDraftAttachmentsAsync([
            new DraftAttachment("one.txt", "text/plain", Convert.ToBase64String("one"u8.ToArray()), 3),
            new DraftAttachment("two.png", "image/png", "AQID", 3)]));
        cut.FindAll("[data-testid='attachment-chip']").Count.ShouldBe(2);
        cut.Find("[data-testid='attachment-remove']").Click();
        cut.FindAll("[data-testid='attachment-chip']").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Paste_callback_adds_image_and_validation_is_announced()
    {
        var cut = Render();
        await cut.InvokeAsync(() => cut.Instance.OnAttachmentsPasted([
            new DraftAttachment("clipboard.png", "image/png", "AQID", 3)]));
        cut.Markup.ShouldContain("clipboard.png");

        await cut.InvokeAsync(() => cut.Instance.OnAttachmentsPasted([
            new DraftAttachment("huge.png", "image/png", "AQID", AttachmentLimits.MaxFileBytes + 1)]));
        var alert = cut.Find("[role='alert']");
        alert.TextContent.ShouldContain("too large");
    }

    [Fact]
    public async Task Attachment_only_send_carries_metadata_and_clears_draft()
    {
        var cut = Render();
        var attachment = new DraftAttachment("notes.txt", "text/plain", Convert.ToBase64String("hello"u8.ToArray()), 5);
        await cut.InvokeAsync(() => cut.Instance.AddDraftAttachmentsAsync([attachment]));
        cut.Find("[data-testid='chat-send']").Click();
        await _interaction.Received(1).SendMessageAsync("agent-1", string.Empty, Arg.Is<IReadOnlyList<DraftAttachment>>(x => x.Count == 1 && x[0].FileName == "notes.txt"));
        cut.FindAll("[data-testid='attachment-chip']").ShouldBeEmpty();
    }

    private IRenderedComponent<ChatPanel> Render() => _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));
}
