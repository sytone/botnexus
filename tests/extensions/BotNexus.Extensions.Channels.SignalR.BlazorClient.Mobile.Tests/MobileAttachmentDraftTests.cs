using System.Text.RegularExpressions;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class MobileAttachmentDraftTests : IDisposable
{
    private const string AgentId = "agent-1";
    private const string ConversationId = "conv-1";
    private readonly BunitContext _context = new();
    private readonly ClientStateStore _store = new();
    private readonly IAgentInteractionService _interaction = Substitute.For<IAgentInteractionService>();

    public MobileAttachmentDraftTests()
    {
        _store.UpsertAgent(new AgentState { AgentId = AgentId, DisplayName = "Agent", IsConnected = true });
        _store.SeedConversations(AgentId, [new ConversationSummaryDto(
            ConversationId, AgentId, "Test", false, "Active", "session-1", 0,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        _store.SelectView(AgentId, ConversationId, SelectionSource.RouteNavigation);
        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(true);
        portalLoad.IsSignalRConnected.Returns(true);
        portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _context.Services.AddSingleton<IClientStateStore>(_store);
        _context.Services.AddSingleton(portalLoad);
        _context.Services.AddSingleton(_interaction);
        _context.Services.AddSingleton<ISlashCommandDispatcher>(new SlashCommandDispatcher(_interaction));
        _context.Services.AddSingleton(new MobileHubTuningOptions());
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public void Default_render_shows_file_and_camera_pickers_without_drafts()
    {
        var cut = Render();
        cut.Find("[data-testid='mobile-attach']");
        cut.Find("[data-testid='mobile-attachment-input'][multiple]");
        var camera = cut.Find("[data-testid='mobile-camera-input']");
        camera.GetAttribute("accept").ShouldBe("image/*");
        camera.GetAttribute("capture").ShouldBe("environment");
        cut.FindAll("[data-testid='mobile-attachment-chip']").ShouldBeEmpty();
    }

    [Fact]
    public async Task Drafts_are_removable_and_reject_only_offending_items_with_shared_limits()
    {
        var cut = Render();
        await cut.InvokeAsync(() => cut.Instance.AddDraftAttachmentsAsync([
            new DraftAttachment("one.txt", "text/plain", "AQ==", 1),
            new DraftAttachment("two.png", "image/png", "Ag==", 1)]));
        cut.FindAll("[data-testid='mobile-attachment-chip']").Count.ShouldBe(2);
        cut.Find("[data-testid='mobile-attachment-remove']").Click();
        cut.FindAll("[data-testid='mobile-attachment-chip']").Count.ShouldBe(1);

        await cut.InvokeAsync(() => cut.Instance.AddDraftAttachmentsAsync([
            new DraftAttachment("huge.bin", "application/octet-stream", "", AttachmentLimits.MaxFileBytes + 1),
            new DraftAttachment("accepted-after-error.txt", "text/plain", "Aw==", 1)]));

        cut.Find("[role='alert']").TextContent.ShouldContain("too large");
        cut.FindAll("[data-testid='mobile-attachment-chip']").Count.ShouldBe(2);
        cut.Markup.ShouldContain("accepted-after-error.txt");
    }

    [Fact]
    public async Task Attachment_only_send_passes_snapshot_and_clears_after_success()
    {
        var cut = Render();
        var attachment = new DraftAttachment("notes.txt", "text/plain", "aGVsbG8=", 5);
        await cut.InvokeAsync(() => cut.Instance.AddDraftAttachmentsAsync([attachment]));
        cut.Find("[data-testid='mobile-send']").Click();
        await _interaction.Received(1).DeliverMessageAsync(AgentId, ConversationId, string.Empty,
            InboundDeliveryMode.Auto,
            Arg.Is<IReadOnlyList<DraftAttachment>>(items => items.Count == 1 && items[0] == attachment));
        cut.FindAll("[data-testid='mobile-attachment-chip']").ShouldBeEmpty();
    }

    [Fact]
    public async Task Failed_send_retains_text_and_attachments_and_shows_error()
    {
        _interaction.DeliverMessageAsync(AgentId, ConversationId, "keep text", InboundDeliveryMode.Auto,
                Arg.Any<IReadOnlyList<DraftAttachment>>())
            .Returns(_ =>
            {
                _store.GetAgent(AgentId)!.Conversations[ConversationId]
                    .AppendMessage(new ChatMessage("Error", "Send failed: offline", DateTimeOffset.UtcNow));
                return Task.CompletedTask;
            });
        var cut = Render();
        cut.Find(".input-textarea").Input(" keep text ");
        await cut.InvokeAsync(() => cut.Instance.AddDraftAttachmentsAsync([
            new DraftAttachment("retry.txt", "text/plain", "AQ==", 1)]));
        cut.Find("[data-testid='mobile-send']").Click();
        cut.WaitForAssertion(() => cut.Find("[role='alert']").TextContent.ShouldContain("offline"));
        cut.Find(".input-textarea").GetAttribute("value").ShouldBe(" keep text ");
        cut.FindAll("[data-testid='mobile-attachment-chip']").Count.ShouldBe(1);
    }

    [Fact]
    public void Source_contract_names_attachment_argument_and_shared_limits_non_vacuously()
    {
        var source = File.ReadAllText(FindChatSource());
        AssertAttachmentSourceContract(source);
        var severedArgument = new Regex(@"deliveryMode\s*,\s*attachments").Replace(source, "deliveryMode, null", 1);
        Should.Throw<ShouldAssertException>(() => AssertAttachmentSourceContract(severedArgument))
            .Message.ShouldContain("attachments argument");
        foreach (var limit in new[] { "MaxCount", "MaxFileBytes", "MaxTotalBytes" })
        {
            var severedLimit = source.Replace($"AttachmentLimits.{limit}", $"Local{limit}", StringComparison.Ordinal);
            Should.Throw<ShouldAssertException>(() => AssertAttachmentSourceContract(severedLimit))
                .Message.ShouldContain($"AttachmentLimits.{limit}");
        }
    }

    private static void AssertAttachmentSourceContract(string source)
    {
        Regex.IsMatch(source, @"DeliverMessageAsync\([\s\S]*?deliveryMode\s*,\s*attachments\)")
            .ShouldBeTrue("DeliverMessageAsync must receive the immutable attachments argument");
        source.Contains("AttachmentLimits.MaxCount", StringComparison.Ordinal).ShouldBeTrue("mobile must use AttachmentLimits.MaxCount");
        source.Contains("AttachmentLimits.MaxFileBytes", StringComparison.Ordinal).ShouldBeTrue("mobile must use AttachmentLimits.MaxFileBytes");
        source.Contains("AttachmentLimits.MaxTotalBytes", StringComparison.Ordinal).ShouldBeTrue("mobile must use AttachmentLimits.MaxTotalBytes");
    }

    private static string FindChatSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "extensions",
                "BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile", "Pages", "Chat.razor");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate mobile Pages/Chat.razor from test output.");
    }

    private IRenderedComponent<Chat> Render() => _context.Render<Chat>(parameters => parameters
        .Add(component => component.AgentId, AgentId)
        .Add(component => component.ConversationId, ConversationId));
}
