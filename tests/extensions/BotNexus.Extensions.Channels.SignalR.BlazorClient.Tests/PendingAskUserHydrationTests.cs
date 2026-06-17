using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

using NSubstitute;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for the per-conversation pending <c>ask_user</c> REST hydration wiring (#1488, ask_user
/// durability Step 1/5). Mirrors <see cref="TodoPerConversationTests"/>: selecting a conversation
/// hydrates the pending prompt from REST when nothing is already pending, and the persisted
/// <c>AskUserRequest</c> JSON maps back to an <see cref="AskUserPromptState"/> the chat panel can render.
/// </summary>
public sealed class PendingAskUserHydrationTests
{
    // A serialized AskUserRequest exactly as AskUserTool persists it: camelCase fields, the input
    // type as a string (JsonStringEnumConverter on the enum), value-object ids as plain strings,
    // the timeout as an ISO duration, and the extra session/agent fields that the client ignores.
    private const string PersistedPrompt =
        "{\"requestId\":\"req-1\",\"conversationId\":\"conv-5\",\"sessionId\":\"sess-1\",\"agentId\":\"agent-5\"," +
        "\"prompt\":\"Pick a deploy target\",\"inputType\":\"SingleChoice\"," +
        "\"choices\":[{\"value\":\"prod\",\"label\":\"Production\",\"description\":\"live\"}," +
        "{\"value\":\"staging\",\"label\":\"Staging\",\"description\":null}]," +
        "\"allowMultiple\":false,\"allowFreeForm\":false,\"timeout\":\"00:05:00\"}";

    private readonly ClientStateStore _store = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();
    private readonly GatewayHubConnection _hub = new();
    private readonly AgentInteractionService _interaction;

    public PendingAskUserHydrationTests()
    {
        _interaction = new AgentInteractionService(_store, _hub, _restClient);
    }

    private AgentState SeedAgent(string agentId)
    {
        _store.SeedAgents([new AgentSummary(agentId, "Test Agent")]);
        return _store.GetAgent(agentId)!;
    }

    private static ConversationState SeedConversation(AgentState agent, string convId)
    {
        var conv = new ConversationState
        {
            ConversationId = convId,
            Title = "Test",
            IsDefault = false,
            Status = "Active",
        };
        agent.Conversations[convId] = conv;
        return conv;
    }

    // ── TryBuildAskUserPromptFromPersistedJson maps the persisted shape ────

    [Fact]
    public void TryBuildAskUserPromptFromPersistedJson_MapsAllFields()
    {
        var ok = GatewayEventHandler.TryBuildAskUserPromptFromPersistedJson(PersistedPrompt, "conv-5", out var prompt);

        Assert.True(ok);
        Assert.NotNull(prompt);
        Assert.Equal("req-1", prompt!.RequestId);
        Assert.Equal("conv-5", prompt.ConversationId);
        Assert.Equal("Pick a deploy target", prompt.Prompt);
        Assert.Equal("SingleChoice", prompt.InputType);
        Assert.False(prompt.AllowMultiple);
        Assert.False(prompt.AllowFreeForm);
        Assert.NotNull(prompt.Choices);
        Assert.Equal(2, prompt.Choices!.Count);
        Assert.Equal("prod", prompt.Choices[0].Value);
        Assert.Equal("Production", prompt.Choices[0].Label);
        // A timeout in the payload becomes an absolute expiry in the future.
        Assert.NotNull(prompt.ExpiresAt);
        Assert.True(prompt.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void TryBuildAskUserPromptFromPersistedJson_FallsBackToConversationId_WhenPayloadOmitsIt()
    {
        const string noConvId =
            "{\"requestId\":\"r9\",\"prompt\":\"Type a value\",\"inputType\":\"FreeForm\",\"allowFreeForm\":true}";

        var ok = GatewayEventHandler.TryBuildAskUserPromptFromPersistedJson(noConvId, "conv-fallback", out var prompt);

        Assert.True(ok);
        Assert.Equal("conv-fallback", prompt!.ConversationId);
        Assert.True(prompt.AllowFreeForm);
        // No timeout in the payload -> no expiry.
        Assert.Null(prompt.ExpiresAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")] // missing required requestId/prompt/inputType
    [InlineData("{\"requestId\":\"r\",\"inputType\":\"FreeForm\"}")] // missing prompt
    [InlineData("{\"requestId\":\"r\",\"prompt\":\"p\"}")] // missing inputType
    public void TryBuildAskUserPromptFromPersistedJson_ReturnsFalse_ForMissingOrMalformed(string? json)
    {
        var ok = GatewayEventHandler.TryBuildAskUserPromptFromPersistedJson(json, "conv-x", out var prompt);

        Assert.False(ok);
        Assert.Null(prompt);
    }

    // ── SelectConversation hydrates pending prompt from REST ──────────────

    [Fact]
    public async Task SelectConversationAsync_HydratesPendingPromptFromRest_WhenNonePending()
    {
        var agent = SeedAgent("agent-5");
        SeedConversation(agent, "conv-5");

        _restClient.GetConversationPendingAskUserAsync("agent-5", "conv-5", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(PersistedPrompt));

        await _interaction.SelectConversationAsync("agent-5", "conv-5");

        var hydrated = _store.GetPendingAskUser("conv-5");
        Assert.NotNull(hydrated);
        Assert.Equal("req-1", hydrated!.RequestId);
        Assert.Equal("Pick a deploy target", hydrated.Prompt);
    }

    [Fact]
    public async Task SelectConversationAsync_DoesNotOverwriteLivePendingPrompt()
    {
        var agent = SeedAgent("agent-6");
        SeedConversation(agent, "conv-6");

        // A live prompt is already pending locally (caught via the live UserInputRequired event).
        _store.SetPendingAskUser(new AskUserPromptState
        {
            RequestId = "live-req",
            ConversationId = "conv-6",
            Prompt = "Live prompt",
            InputType = "FreeForm"
        });

        await _interaction.SelectConversationAsync("agent-6", "conv-6");

        // REST hydration must be skipped entirely so the live prompt is preserved.
        await _restClient.DidNotReceive().GetConversationPendingAskUserAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal("live-req", _store.GetPendingAskUser("conv-6")!.RequestId);
    }

    [Fact]
    public async Task SelectConversationAsync_LeavesNoPrompt_WhenRestReturnsNone()
    {
        var agent = SeedAgent("agent-7");
        SeedConversation(agent, "conv-7");

        _restClient.GetConversationPendingAskUserAsync("agent-7", "conv-7", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        await _interaction.SelectConversationAsync("agent-7", "conv-7");

        Assert.Null(_store.GetPendingAskUser("conv-7"));
    }
}
