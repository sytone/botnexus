using Bunit;
using System.Text.Json;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Component-level tests for the <c>submitToAgent</c> bridge verb (#2449): the SDK surface injected
/// into the iframe, and the guarantee that <see cref="CanvasPanel.HandleCanvasMessage"/> derives the
/// target conversation from its OWN binding and ignores any conversation id the iframe supplies.
/// </summary>
public sealed class CanvasSubmitBridgeTests : IDisposable
{
    private const string BoundConversation = "conv-bound";

    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly RecordingInteraction _interaction = new();

    public CanvasSubmitBridgeTests()
    {
        _store.SeedAgents([new AgentSummary("agent-1", "Alpha")]);
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri("http://localhost/") });
        _ctx.Services.AddSingleton<IAgentInteractionService>(_interaction);
        _ctx.JSInterop.SetupVoid("canvasBridge.register", _ => true);
        _ctx.JSInterop.SetupVoid("canvasBridge.unregister", _ => true);
        _ctx.JSInterop.SetupVoid("canvasBridge.respond", _ => true);
    }

    public void Dispose() => _ctx.Dispose();

    private CanvasPanel RenderPanel()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.CanvasHtml = "<html><head></head><body>canvas</body></html>";
        return _ctx.Render<CanvasPanel>(p => p
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.ConversationId, BoundConversation)).Instance;
    }

    [Fact]
    public void Bridge_sdk_exposes_submitToAgent_alongside_the_other_five_verbs()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.CanvasHtml = "<html><head></head><body>canvas</body></html>";

        var cut = _ctx.Render<CanvasPanel>(p => p
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.ConversationId, BoundConversation));

        var srcdoc = cut.Find("iframe[data-testid='canvas-iframe']").GetAttribute("srcdoc")!;
        srcdoc.ShouldContain("submitToAgent:");
        srcdoc.ShouldContain("canvas-state-submit");
        srcdoc.ShouldContain("getAll:");
        srcdoc.ShouldContain("clear:");
    }

    /// <summary>
    /// The security crux at the component seam: a canvas that puts a foreign conversation id in the
    /// postMessage payload must still be routed to the bound conversation.
    /// </summary>
    [Fact]
    public async Task Submit_ignores_a_conversation_id_supplied_by_the_iframe()
    {
        var panel = RenderPanel();

        var json = JsonSerializer.Serialize(new
        {
            type = "canvas-state-submit",
            requestId = "req_1",
            conversationId = "conv-someone-else",
            prompt = "read my form"
        });
        await panel.HandleCanvasMessage(json);

        _interaction.Calls.Count.ShouldBe(1);
        _interaction.Calls[0].ConversationId.ShouldBe(BoundConversation);
        _interaction.Calls[0].AgentId.ShouldBe("agent-1");
        _interaction.Calls[0].Prompt.ShouldBe("read my form");
    }

    [Fact]
    public async Task Submit_forwards_optional_instructions()
    {
        var panel = RenderPanel();

        var json = JsonSerializer.Serialize(new
        {
            type = "canvas-state-submit",
            requestId = "req_2",
            prompt = "done",
            instructions = "read answer1"
        });
        await panel.HandleCanvasMessage(json);

        _interaction.Calls.Single().Instructions.ShouldBe("read answer1");
    }

    /// <summary>Non-string payload fields are not smuggled through as a prompt.</summary>
    [Fact]
    public async Task Submit_with_a_non_string_prompt_forwards_null()
    {
        var panel = RenderPanel();

        var json = """{"type":"canvas-state-submit","requestId":"req_3","prompt":{"nested":1}}""";
        await panel.HandleCanvasMessage(json);

        _interaction.Calls.Single().Prompt.ShouldBeNull();
    }

    private sealed record SubmitCall(string AgentId, string ConversationId, string? Prompt, string? Instructions);

    /// <summary>
    /// Records the arguments the component passes through. Every other member throws so an
    /// accidental extra call cannot be silently absorbed.
    /// </summary>
    private sealed class RecordingInteraction : IAgentInteractionService
    {
        public List<SubmitCall> Calls { get; } = [];

        public Task<CanvasSubmitResult> SubmitCanvasPromptAsync(string agentId, string conversationId, string? prompt, string? instructions)
        {
            Calls.Add(new SubmitCall(agentId, conversationId, prompt, instructions));
            return Task.FromResult(CanvasSubmitResult.Ok());
        }

        public Task SendMessageAsync(string agentId, string content) => throw new NotSupportedException();
        public Task SendMessageAsync(string agentId, string content, IReadOnlyList<DraftAttachment> attachments) => throw new NotSupportedException();
        public Task SteerAsync(string agentId, string content) => throw new NotSupportedException();
        public Task SteerAsync(string agentId, string content, IReadOnlyList<DraftAttachment> attachments) => throw new NotSupportedException();
        public Task FollowUpAsync(string agentId, string content) => throw new NotSupportedException();
        public Task FollowUpAsync(string agentId, string content, IReadOnlyList<DraftAttachment> attachments) => throw new NotSupportedException();
        public Task AbortAsync(string agentId) => throw new NotSupportedException();
        public Task InterruptAndSteerAsync(string agentId, string message) => throw new NotSupportedException();
        public Task InterruptAndSteerAsync(string agentId, string message, IReadOnlyList<DraftAttachment> attachments) => throw new NotSupportedException();
        public Task ResetSessionAsync(string agentId) => throw new NotSupportedException();
        public Task<bool> ExecuteGatewayCommandAsync(string agentId, string commandText) => throw new NotSupportedException();
        public Task<CompactSessionResult?> CompactSessionAsync(string agentId) => throw new NotSupportedException();
        public Task<string?> CreateConversationAsync(string agentId, string? title = null, bool select = true) => throw new NotSupportedException();
        public Task SelectConversationAsync(string agentId, string conversationId) => throw new NotSupportedException();
        public Task<int> LoadMoreHistoryAsync(string agentId, string conversationId) => throw new NotSupportedException();
        public Task RenameConversationAsync(string agentId, string? conversationId, string newTitle) => throw new NotSupportedException();
        public Task ArchiveConversationAsync(string agentId, string conversationId) => throw new NotSupportedException();
        public Task SetConversationPinnedAsync(string agentId, string conversationId, bool pinned) => throw new NotSupportedException();
        public Task RefreshAgentsAsync() => throw new NotSupportedException();
        public Task RefreshConversationsAsync(string agentId) => throw new NotSupportedException();
        public Task ViewSubAgentAsync(SubAgentInfo subAgent) => throw new NotSupportedException();
        public Task RespondToAskUserAsync(string conversationId, string requestId, string? freeFormText, string[]? selectedValues, bool cancelled) => throw new NotSupportedException();
        public void ClearLocalMessages(string agentId) => throw new NotSupportedException();
    }
}
