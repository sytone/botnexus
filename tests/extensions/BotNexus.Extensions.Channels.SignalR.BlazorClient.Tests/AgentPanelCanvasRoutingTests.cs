using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Regression tests for fix: AgentPanel must pass ConversationId to CanvasPanel
/// so conversation-scoped canvas HTML is displayed (not the legacy agent-level fallback).
/// </summary>
public sealed class AgentPanelCanvasRoutingTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();

    public AgentPanelCanvasRoutingTests()
    {
        _store.SeedAgents([new AgentSummary("agent-1", "Alpha")]);
        _store.SeedConversations("agent-1", [
            new ConversationSummaryDto(
                ConversationId: "conv-1",
                AgentId: "agent-1",
                Title: "General",
                IsDefault: true,
                Status: "Active",
                ActiveSessionId: null,
                BindingCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow)
        ]);
        _store.SetActiveConversation("agent-1", "conv-1");

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Canvas_tab_renders_conversation_html_when_conversation_has_canvas()
    {
        // Arrange: set canvas HTML on the conversation (not the agent)
        var conv = _store.GetConversation("conv-1")!;
        conv.CanvasHtml = "<h1>Conversation Canvas</h1>";

        var cut = _ctx.Render<CanvasPanel>(parameters =>
        {
            parameters.Add(x => x.AgentId, "agent-1");
            parameters.Add(x => x.ConversationId, "conv-1");
        });

        // Assert: iframe is shown with conversation-scoped HTML
        var iframe = cut.Find("iframe[data-testid='canvas-iframe']");
        var srcdoc = iframe.GetAttribute("srcdoc");
        Assert.NotNull(srcdoc);
        Assert.Contains("<h1>Conversation Canvas</h1>", srcdoc);
    }

    [Fact]
    public void Canvas_tab_shows_empty_state_when_no_conversation_canvas_set()
    {
        // Arrange: no canvas on conversation or agent
        var cut = _ctx.Render<CanvasPanel>(parameters =>
        {
            parameters.Add(x => x.AgentId, "agent-1");
            parameters.Add(x => x.ConversationId, "conv-1");
        });

        // Assert: empty state, no iframe
        cut.Markup.ShouldContain("Canvas output will appear here");
        cut.FindAll("iframe").ShouldBeEmpty();
    }

    [Fact]
    public void AgentPanel_passes_active_conversation_id_to_canvas_panel()
    {
        // Verify that AgentPanel.razor passes ConversationId to CanvasPanel by inspecting
        // the razor source file (static analysis guard).
        var razorPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "Components",
            "AgentPanel.razor");

        var source = File.ReadAllText(razorPath);

        // The CanvasPanel declaration must include ConversationId
        source.ShouldContain("ConversationId=");
        // It must reference the active conversation via Agent?.ActiveConversationId
        source.ShouldContain("ActiveConversationId");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Unable to locate BotNexus.slnx from test base directory.");
    }
}
