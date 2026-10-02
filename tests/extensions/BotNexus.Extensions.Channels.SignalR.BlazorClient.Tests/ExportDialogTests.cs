using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ExportDialogTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly IGatewayRestClient _rest = Substitute.For<IGatewayRestClient>();

    public ExportDialogTests()
    {
        var interaction = Substitute.For<IAgentInteractionService>();
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(interaction);
        _ctx.Services.AddSingleton(_rest);
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(new SlashCommandDispatcher(interaction));
        var preferences = Substitute.For<IPortalPreferencesService>();
        preferences.Current.Returns(new PortalPreferences());
        _ctx.Services.AddSingleton(preferences);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        _store.UpsertAgent(new AgentState { AgentId = "agent-1", DisplayName = "Agent" });
        _store.SeedConversations("agent-1",
        [
            new ConversationSummaryDto("c-1", "agent-1", "Review", false, "Active", "s-1", 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.SetActiveConversation("agent-1", "c-1");
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void ExportAction_OpensDialogWithTruthfulDefaults()
    {
        var cut = Render();

        cut.Find("[data-testid='chat-export-btn']").Click();

        cut.Find("[data-testid='export-dialog']");
        cut.Find("[data-testid='export-include-tools']").GetAttribute("checked").ShouldNotBeNull();
        cut.Find("[data-testid='export-include-thinking']").GetAttribute("checked").ShouldBeNull();
        cut.Find("[data-testid='export-include-system']").GetAttribute("checked").ShouldBeNull();
        cut.Find("[data-testid='export-redact-secrets']").GetAttribute("checked").ShouldNotBeNull();
        cut.Markup.ShouldContain("privacy");
        cut.Markup.ShouldContain("Whole conversation");
    }

    [Fact]
    public async Task Download_UsesRestClientAndBrowserDownloadInterop()
    {
        _rest.ExportConversationAsync("c-1", Arg.Any<ConversationExportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExportDownload("review.md", "text/markdown", [1, 2, 3]));
        var cut = Render();
        cut.Find("[data-testid='chat-export-btn']").Click();

        await cut.Find("[data-testid='export-download-btn']").ClickAsync(new());

        await _rest.Received(1).ExportConversationAsync(
            "c-1",
            Arg.Is<ConversationExportRequest>(r => r.Format == "markdown" && r.IncludeTools && !r.IncludeThinking && !r.IncludeSystemMessages && r.RedactSecrets),
            Arg.Any<CancellationToken>());
        _ctx.JSInterop.VerifyInvoke("BotNexus.downloadFile");
        cut.FindAll("[data-testid='export-dialog']").ShouldBeEmpty();
    }

    [Fact]
    public async Task ChangedOptions_AreSentToTheExportRoute()
    {
        _rest.ExportConversationAsync("c-1", Arg.Any<ConversationExportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExportDownload("review.html", "text/html", [1]));
        var cut = Render();
        cut.Find("[data-testid='chat-export-btn']").Click();

        cut.Find("[data-testid='export-format']").Change("html");
        cut.Find("[data-testid='export-include-tools']").Change(false);
        cut.Find("[data-testid='export-include-thinking']").Change(true);
        cut.Find("[data-testid='export-include-system']").Change(true);
        cut.Find("[data-testid='export-redact-secrets']").Change(false);
        await cut.Find("[data-testid='export-download-btn']").ClickAsync(new());

        await _rest.Received(1).ExportConversationAsync(
            "c-1",
            Arg.Is<ConversationExportRequest>(r => r.Format == "html" && !r.IncludeTools && r.IncludeThinking && r.IncludeSystemMessages && !r.RedactSecrets),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelectedMessages_UsesContiguousVisibleRange()
    {
        var conversation = _store.GetConversation("c-1") ?? throw new InvalidOperationException("Expected seeded conversation.");
        conversation.AppendMessage(new ChatMessage("User", "first", DateTimeOffset.UtcNow) { ServerEntryId = "s-1#1" });
        conversation.AppendMessage(new ChatMessage("Assistant", "middle", DateTimeOffset.UtcNow) { ServerEntryId = "s-1#2" });
        conversation.AppendMessage(new ChatMessage("User", "last", DateTimeOffset.UtcNow) { ServerEntryId = "s-1#3" });
        _rest.ExportConversationAsync("c-1", Arg.Any<ConversationExportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExportDownload("excerpt.md", "text/markdown", [1]));
        var cut = Render();
        cut.Find("[data-testid='chat-export-btn']").Click();

        cut.Find("[data-testid='export-scope']").Change("selected");
        var selectors = cut.FindAll("[data-testid='export-message-selector']");
        selectors.Count.ShouldBe(3);
        selectors[0].Click();
        cut.FindAll("[data-testid='export-message-selector']")[2].Click();

        cut.Find("[data-testid='export-preview']").TextContent.ShouldContain("3 visible messages");
        await cut.Find("[data-testid='export-download-btn']").ClickAsync(new());
        await _rest.Received(1).ExportConversationAsync(
            "c-1",
            Arg.Is<ConversationExportRequest>(r => r.FirstEntryId == "s-1#1" && r.LastEntryId == "s-1#3"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CurrentSession_UsesSessionExportRoute()
    {
        _rest.ExportSessionAsync("s-1", Arg.Any<ConversationExportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExportDownload("session.md", "text/markdown", [1]));
        var cut = Render();
        cut.Find("[data-testid='chat-export-btn']").Click();
        cut.Find("[data-testid='export-scope']").Change("session");

        await cut.Find("[data-testid='export-download-btn']").ClickAsync(new());

        await _rest.Received(1).ExportSessionAsync("s-1", Arg.Any<ConversationExportRequest>(), Arg.Any<CancellationToken>());
        await _rest.DidNotReceive().ExportConversationAsync(
            Arg.Any<string>(), Arg.Any<ConversationExportRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancel_ClearsSelectionAndPerformsNoRequest()
    {
        var conversation = _store.GetConversation("c-1") ?? throw new InvalidOperationException("Expected seeded conversation.");
        conversation.AppendMessage(new ChatMessage("User", "first", DateTimeOffset.UtcNow) { ServerEntryId = "s-1#1" });
        var cut = Render();
        cut.Find("[data-testid='chat-export-btn']").Click();
        cut.Find("[data-testid='export-scope']").Change("selected");
        cut.Find("[data-testid='export-message-selector']").Click();

        cut.Find("[data-testid='export-cancel-btn']").Click();
        cut.Find("[data-testid='chat-export-btn']").Click();
        cut.Find("[data-testid='export-scope']").Change("selected");

        cut.Find("[data-testid='export-preview']").TextContent.ShouldContain("Choose the first message");
        await _rest.DidNotReceiveWithAnyArgs().ExportConversationAsync(default!, default!, default);
        await _rest.DidNotReceiveWithAnyArgs().ExportSessionAsync(default!, default!, default);
    }

    [Fact]
    public async Task Failure_IsVisibleAndBusyStateClears()
    {
        _rest.ExportConversationAsync("c-1", Arg.Any<ConversationExportRequest>(), Arg.Any<CancellationToken>())
            .Returns((ExportDownload?)null);
        var cut = Render();
        cut.Find("[data-testid='chat-export-btn']").Click();

        await cut.Find("[data-testid='export-download-btn']").ClickAsync(new());

        cut.Find("[data-testid='export-error']");
        cut.Find("[data-testid='export-download-btn']").HasAttribute("disabled").ShouldBeFalse();
    }

    private IRenderedComponent<ChatPanel> Render() =>
        _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));
}
