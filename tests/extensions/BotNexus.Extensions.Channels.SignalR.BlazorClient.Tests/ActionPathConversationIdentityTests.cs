using System.Reflection;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #3211 (step 4 of epic #3061): the portal action paths must take conversation identity as an
/// explicit argument instead of re-deriving it from ambient <c>AgentState.ActiveConversationId</c>.
/// </summary>
/// <remarks>
/// The deleted <c>TryResolveActiveConversationTarget</c> made the ambient conversation the ONLY
/// possible target, so a user who deep-linked to an older conversation while a newer one was the
/// agent's active selection would steer or abort the newer one silently. These tests deliberately
/// arrange exactly that divergence - <c>ActiveConversationId</c> points at the most-recent
/// conversation, the action names the older deep-linked one - and assert the action lands on the
/// conversation it was TOLD about. They fail against the pre-#3211 shape.
/// </remarks>
public sealed class ActionPathConversationIdentityTests
{
    private const string AgentId = "agent-1";

    /// <summary>The conversation the URL deep-links to. NOT the agent's active selection.</summary>
    private const string DeepLinkedConversationId = "conv-old";

    /// <summary>The most-recent conversation, which ambient state points at.</summary>
    private const string MostRecentConversationId = "conv-new";

    private readonly ClientStateStore _store = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();
    private readonly AgentInteractionService _service;

    public ActionPathConversationIdentityTests()
    {
        _service = new AgentInteractionService(
            _store,
            new GatewayHubConnection(),
            _restClient,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentInteractionService>.Instance);

        _store.UpsertAgent(new AgentState { AgentId = AgentId, DisplayName = "Agent 1", IsConnected = true });

        var agent = _store.GetAgent(AgentId)!;
        agent.Conversations[DeepLinkedConversationId] = new ConversationState
        {
            ConversationId = DeepLinkedConversationId,
            Title = "Older, deep-linked",
            ActiveSessionId = "sess-old",
            HistoryLoaded = true
        };
        agent.Conversations[MostRecentConversationId] = new ConversationState
        {
            ConversationId = MostRecentConversationId,
            Title = "Most recent",
            ActiveSessionId = "sess-new",
            HistoryLoaded = true
        };

        // Ambient state points at the MOST RECENT conversation - the pre-#3211 fallback target.
        agent.ActiveConversationId = MostRecentConversationId;
    }

    private ConversationState DeepLinked => _store.GetAgent(AgentId)!.Conversations[DeepLinkedConversationId];
    private ConversationState MostRecent => _store.GetAgent(AgentId)!.Conversations[MostRecentConversationId];

    [Fact]
    public async Task SteerAsync_targets_the_named_conversation_not_the_ambient_active_one()
    {
        // The hub is disconnected, so the steer echo lands locally and then an error row follows.
        // Both belong to the conversation that was NAMED, never to the ambient active one.
        await _service.SteerAsync(AgentId, DeepLinkedConversationId, "steer the old thread");

        DeepLinked.Messages.ShouldNotBeEmpty();
        DeepLinked.Messages[0].Role.ShouldBe("User");
        DeepLinked.Messages[0].Content.ShouldContain("steer the old thread");

        // The most-recent conversation - the pre-fix ambient target - is untouched.
        MostRecent.Messages.ShouldBeEmpty();

        // The steering-queue chip is also scoped to the targeted conversation.
        _store.GetSteeringQueue(DeepLinkedConversationId).ShouldNotBeEmpty();
        _store.GetSteeringQueue(MostRecentConversationId).ShouldBeEmpty();
    }

    [Fact]
    public async Task AbortAsync_clears_the_named_conversations_run_state_not_the_ambient_one()
    {
        foreach (var conv in new[] { DeepLinked, MostRecent })
        {
            conv.StreamState.IsRunActive = true;
            conv.StreamState.IsStreaming = true;
        }
        _store.AddSteeringEntry(DeepLinkedConversationId,
            new SteeringEntry("f-old", "queued", SteeringEntryKind.FollowUp, SteeringEntryStatus.Pending));
        _store.AddSteeringEntry(MostRecentConversationId,
            new SteeringEntry("f-new", "queued", SteeringEntryKind.FollowUp, SteeringEntryStatus.Pending));

        await _service.AbortAsync(AgentId, DeepLinkedConversationId);

        // The named conversation's run bracket and pending chip are torn down...
        DeepLinked.StreamState.IsRunActive.ShouldBeFalse();
        DeepLinked.StreamState.IsStreaming.ShouldBeFalse();
        _store.GetSteeringQueue(DeepLinkedConversationId).ShouldBeEmpty();

        // ...and the ambient active conversation's are NOT, which is exactly what the pre-#3211
        // ambient resolution got backwards.
        MostRecent.StreamState.IsRunActive.ShouldBeTrue();
        _store.GetSteeringQueue(MostRecentConversationId).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ResetSessionAsync_reports_failure_into_the_named_conversation()
    {
        await _service.ResetSessionAsync(AgentId, DeepLinkedConversationId);

        DeepLinked.Messages.ShouldNotBeEmpty();
        DeepLinked.Messages[^1].Role.ShouldBe("Error");
        MostRecent.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task InterruptAndSteerAsync_targets_the_named_conversation()
    {
        await _service.InterruptAndSteerAsync(AgentId, DeepLinkedConversationId, "redirect the old thread");

        DeepLinked.Messages.ShouldNotBeEmpty();
        DeepLinked.Messages[0].Content.ShouldContain("redirect the old thread");
        MostRecent.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task ActionPaths_are_a_no_op_when_the_named_conversation_is_unknown()
    {
        // No silent fallback to the ambient conversation: an unknown id means "nothing to act on".
        await _service.SteerAsync(AgentId, "conv-does-not-exist", "should go nowhere");
        await _service.AbortAsync(AgentId, "conv-does-not-exist");

        DeepLinked.Messages.ShouldBeEmpty();
        MostRecent.Messages.ShouldBeEmpty();
    }

    /// <summary>
    /// AC1 fence: every action method on the interface carries an explicit conversation id, and AC2
    /// - the ambient resolver is gone from the Core assembly. A future action added without a
    /// conversation parameter fails here rather than silently reintroducing ambient targeting.
    /// </summary>
    [Fact]
    public void Every_action_method_on_the_interface_takes_an_explicit_conversationId()
    {
        string[] actionMethods =
        [
            nameof(IAgentInteractionService.SendMessageAsync),
            nameof(IAgentInteractionService.SteerAsync),
            nameof(IAgentInteractionService.FollowUpAsync),
            nameof(IAgentInteractionService.AbortAsync),
            nameof(IAgentInteractionService.InterruptAndSteerAsync),
            nameof(IAgentInteractionService.ResetSessionAsync),
            nameof(IAgentInteractionService.CompactSessionAsync),
            nameof(IAgentInteractionService.ExecuteGatewayCommandAsync),
            nameof(IAgentInteractionService.ClearLocalMessages),
        ];

        var offenders = typeof(IAgentInteractionService)
            .GetMethods()
            .Where(m => actionMethods.Contains(m.Name))
            .Where(m => !m.GetParameters().Any(p => p.Name == "conversationId"))
            .Select(m => m.Name)
            .ToArray();

        offenders.ShouldBeEmpty(
            $"every action overload must take an explicit conversationId (#3211): {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TryResolveActiveConversationTarget_no_longer_exists_in_the_core_assembly()
    {
        var survivors = typeof(AgentInteractionService).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.Name == "TryResolveActiveConversationTarget")
            .Select(m => $"{m.DeclaringType!.FullName}.{m.Name}")
            .ToArray();

        survivors.ShouldBeEmpty(
            "#3211 AC2: the ambient conversation resolver must not exist in BlazorClient.Core.");
    }
}
