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
