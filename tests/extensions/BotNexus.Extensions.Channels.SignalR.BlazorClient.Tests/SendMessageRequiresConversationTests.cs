using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Issue #3063: the client send path takes the conversation as a REQUIRED parameter instead of
/// re-deriving it from ambient <c>AgentState.ActiveConversationId</c>.
///
/// These tests pin the two clauses of AC6 - the supplied id is the one that reaches the transport,
/// and sending never creates a conversation as a side effect - plus the sad paths that used to be
/// papered over by the ambient fallback.
///
/// <para>
/// The hub in these tests is a real but unconnected <see cref="GatewayHubConnection"/>, so
/// <c>InvokeAsync</c> throws. That is deliberate and is what makes the assertions sharp: the send
/// path has already done all of its conversation resolution by the time it reaches the transport,
/// so the LOCAL evidence (which conversation received the user echo, which received the resulting
/// error row, and whether the REST client was asked to create anything) proves precisely which
/// conversation the send targeted. A send that resolved a different conversation would leave its
/// rows somewhere else.
/// </para>
/// </summary>
public sealed class SendMessageRequiresConversationTests
{
    private const string AgentId = "agent-1";
    private const string Target = "conv-target";
    private const string Other = "conv-other";

    private readonly ClientStateStore _store = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();
    private readonly AgentInteractionService _service;

    public SendMessageRequiresConversationTests()
    {
        _service = new AgentInteractionService(
            _store,
            new GatewayHubConnection(),
            _restClient,
            NullLogger<AgentInteractionService>.Instance);

        _store.UpsertAgent(new AgentState { AgentId = AgentId, DisplayName = "Agent 1", IsConnected = true });
        var agent = _store.GetAgent(AgentId)!;

        // The AMBIENT selection points at `Other`, deliberately NOT at the conversation the tests
        // send into. Any test that lands rows in `Other` has caught an ambient re-read.
        agent.ActiveConversationId = Other;
        agent.Conversations[Other] = new ConversationState { ConversationId = Other, Title = "Other", HistoryLoaded = true };
        agent.Conversations[Target] = new ConversationState { ConversationId = Target, Title = "Target", HistoryLoaded = true };
    }

    private ConversationState TargetConv => _store.GetAgent(AgentId)!.Conversations[Target];
    private ConversationState OtherConv => _store.GetAgent(AgentId)!.Conversations[Other];

    // ── AC6: the supplied conversation is the one that is targeted ───────────

    /// <summary>
    /// The explicit id wins over the ambient selection. The user echo lands in the named
    /// conversation and the ambient one is untouched.
    /// </summary>
    [Fact]
    public async Task Send_targets_the_supplied_conversation_not_the_ambient_selection()
    {
        await _service.SendMessageAsync(AgentId, Target, "hello");

        TargetConv.Messages.ShouldContain(m => m.Role == "User" && m.Content == "hello");
        OtherConv.Messages.ShouldNotContain(m => m.Role == "User" && m.Content == "hello");
    }

    /// <summary>
    /// The transport failure row must follow the SAME conversation the send targeted. Before #3063
    /// the error append resolved its own target through ambient state, so a send into a
    /// non-selected conversation reported its failure into a different transcript.
    /// </summary>
    [Fact]
    public async Task Send_failure_is_reported_into_the_supplied_conversation()
    {
        await _service.SendMessageAsync(AgentId, Target, "hello");

        TargetConv.Messages.ShouldContain(m => m.Role == "Error");
        OtherConv.Messages.ShouldNotContain(m => m.Role == "Error");
    }

    /// <summary>The attachment overload resolves its conversation the same way.</summary>
    [Fact]
    public async Task Send_with_attachments_targets_the_supplied_conversation()
    {
        await _service.SendMessageAsync(
            AgentId, Target, "with file",
            [new DraftAttachment("notes.txt", "text/plain", Convert.ToBase64String("hi"u8.ToArray()), 2)]);

        TargetConv.Messages.ShouldContain(m => m.Role == "User" && m.Content == "with file");
        OtherConv.Messages.ShouldBeEmpty();
    }

    // ── AC6: no conversation is created as a side effect of sending ──────────

    /// <summary>
    /// The create-on-send block is gone: an agent with NO ambient selection at all does not cause a
    /// conversation to be created. The REST create endpoint must never be reached from a send.
    /// </summary>
    [Fact]
    public async Task Send_never_creates_a_conversation_as_a_side_effect()
    {
        _store.GetAgent(AgentId)!.ActiveConversationId = null;

        await _service.SendMessageAsync(AgentId, Target, "hello");

        await _restClient.DidNotReceiveWithAnyArgs()
            .CreateConversationAsync(default!, default);
        _store.GetAgent(AgentId)!.Conversations.Count.ShouldBe(2, "sending must not add a conversation");
    }

    /// <summary>
    /// A send naming a conversation this agent does not own creates nothing and writes nothing -
    /// it does not silently fall back to the ambient selection, which is the misroute #3063 closes.
    /// </summary>
    [Fact]
    public async Task Send_naming_an_unknown_conversation_writes_nothing_anywhere()
    {
        await _service.SendMessageAsync(AgentId, "conv-does-not-exist", "hello");

        await _restClient.DidNotReceiveWithAnyArgs().CreateConversationAsync(default!, default);
        TargetConv.Messages.ShouldBeEmpty();
        OtherConv.Messages.ShouldBeEmpty();
    }

    // ── Sad paths: a missing conversation is a loud failure, not a fallback ──

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Send_with_a_blank_conversation_id_throws_rather_than_resolving_one(string conversationId)
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _service.SendMessageAsync(AgentId, conversationId, "hello"));

        OtherConv.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Send_with_a_null_conversation_id_throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            () => _service.SendMessageAsync(AgentId, null!, "hello"));
    }

    /// <summary>An unknown AGENT is still a quiet no-op, matching the pre-existing contract.</summary>
    [Fact]
    public async Task Send_for_an_unknown_agent_is_a_no_op()
    {
        await _service.SendMessageAsync("no-such-agent", Target, "hello");

        TargetConv.Messages.ShouldBeEmpty();
        await _restClient.DidNotReceiveWithAnyArgs().CreateConversationAsync(default!, default);
    }
}
