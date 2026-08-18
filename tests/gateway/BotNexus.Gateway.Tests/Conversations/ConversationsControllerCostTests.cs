using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Conversations;

/// <summary>
/// Tests for <c>GET /api/conversations/costs</c> (#2898): the read-time conversation cost rollup.
/// </summary>
/// <remarks>
/// Two paths are covered because they differ in what they can honestly report: a store that
/// implements <see cref="IConversationCostReader"/> measures compactions, and one that does not
/// must report <see langword="null"/> for that field rather than a fabricated zero.
/// </remarks>
public sealed class ConversationsControllerCostTests
{
    private static readonly AgentId Owner = AgentId.From("agent-owner");

    /// <summary>
    /// A store that CAN answer the rollup: an <see cref="ISessionStore"/> substitute that also
    /// implements <see cref="IConversationCostReader"/>, so the controller's capability probe finds
    /// it. Fixed data rather than a real database, because the controller's ranking and merge
    /// behaviour is what is under test here - the SQL itself is covered by the store's own tests.
    /// </summary>
    /// <remarks>
    /// A substitute rather than a subclass: <see cref="InMemorySessionStore"/> is sealed, and
    /// wrapping it would mean hand-forwarding seventeen members that this test never calls.
    /// </remarks>
    private static ISessionStore CostAwareStore(IReadOnlyList<ConversationCostSummary> costs)
    {
        var store = Substitute.For<ISessionStore, IConversationCostReader>();
        ((IConversationCostReader)store)
            .GetConversationCostsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(costs));
        return store;
    }

    /// <summary>
    /// AC1: the endpoint ranks by accumulation, most-expensive first, with a deterministic
    /// tie-break so equal rows never reshuffle between reads.
    /// </summary>
    [Fact]
    public async Task Costs_are_ranked_by_total_descending()
    {
        var conversations = new InMemoryConversationStore();
        var cheap = await conversations.CreateAsync(NewConversation("cheap"));
        var dear = await conversations.CreateAsync(NewConversation("dear"));

        var sessions = CostAwareStore(
        [
            new ConversationCostSummary(cheap.ConversationId.Value, 2, 40, 0),
            new ConversationCostSummary(dear.ConversationId.Value, 527, 8_720_000, 28)
        ]);

        var costs = await InvokeCosts(new ConversationsController(conversations, sessions));

        costs[0].ConversationId.ShouldBe(dear.ConversationId.Value);
        costs[0].SessionCount.ShouldBe(527);
        costs[0].MessageCount.ShouldBe(8_720_000);
        costs[0].CompactionSummaryCount.ShouldBe(28);
        costs[1].ConversationId.ShouldBe(cheap.ConversationId.Value);
    }

    /// <summary>
    /// AC3: a store that cannot count compactions reports <see langword="null"/> - "not measured" -
    /// and never a fabricated <c>0</c>, which would read as "this conversation never compacted".
    /// </summary>
    [Fact]
    public async Task Store_without_the_cost_capability_reports_null_not_zero_compactions()
    {
        var conversations = new InMemoryConversationStore();
        var conv = await conversations.CreateAsync(NewConversation("plain"));

        // InMemorySessionStore does NOT implement IConversationCostReader - the degraded path.
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.Create(), Owner);
        session.ConversationId = conv.ConversationId;
        session.History.Add(new SessionEntry { Role = MessageRole.User, Content = "hello" });
        session.History.Add(new SessionEntry { Role = MessageRole.Assistant, Content = "hi" });
        await sessions.SaveAsync(session);

        var costs = await InvokeCosts(new ConversationsController(conversations, sessions));

        var row = costs.ShouldHaveSingleItem();
        row.ConversationId.ShouldBe(conv.ConversationId.Value);
        row.SessionCount.ShouldBe(1);
        row.MessageCount.ShouldBe(2);
        row.CompactionSummaryCount.ShouldBeNull();
        row.TotalTokens.ShouldBeNull();
    }

    /// <summary>
    /// AC3 (the boundary that matters): a conversation the rollup does not mention has a genuinely
    /// measured zero session count - the session table IS the evidence - while every field the
    /// platform did not measure stays null. The two must not collapse into each other.
    /// </summary>
    [Fact]
    public async Task Conversation_with_no_sessions_reports_measured_zero_but_unmeasured_null()
    {
        var conversations = new InMemoryConversationStore();
        var conv = await conversations.CreateAsync(NewConversation("empty"));

        var sessions = CostAwareStore([]);

        var costs = await InvokeCosts(new ConversationsController(conversations, sessions));

        var row = costs.ShouldHaveSingleItem();
        row.ConversationId.ShouldBe(conv.ConversationId.Value);
        row.SessionCount.ShouldBe(0);
        row.MessageCount.ShouldBe(0);
        row.TotalTokens.ShouldBeNull();
    }

    /// <summary>
    /// Every listed conversation gets exactly one row, including ones absent from the rollup: a
    /// ranking that silently dropped conversations would understate the fleet.
    /// </summary>
    [Fact]
    public async Task Every_listed_conversation_gets_exactly_one_row()
    {
        var conversations = new InMemoryConversationStore();
        var a = await conversations.CreateAsync(NewConversation("a"));
        var b = await conversations.CreateAsync(NewConversation("b"));
        var c = await conversations.CreateAsync(NewConversation("c"));

        var sessions = CostAwareStore(
        [
            new ConversationCostSummary(a.ConversationId.Value, 1, 10, 0),
            // b and c are absent from the rollup.
            new ConversationCostSummary("not-a-listed-conversation", 99, 99, 9)
        ]);

        var costs = await InvokeCosts(new ConversationsController(conversations, sessions));

        costs.Select(r => r.ConversationId).OrderBy(id => id, StringComparer.Ordinal).ShouldBe(
            new[] { a.ConversationId.Value, b.ConversationId.Value, c.ConversationId.Value }
                .OrderBy(id => id, StringComparer.Ordinal));

        // An unlisted conversation in the rollup is not smuggled into the response.
        costs.ShouldNotContain(r => r.ConversationId == "not-a-listed-conversation");
    }

    private static async Task<IReadOnlyList<ConversationCostSummary>> InvokeCosts(
        ConversationsController controller)
    {
        var result = await controller.Costs(CancellationToken.None);
        var ok = result.ShouldBeOfType<OkObjectResult>();
        return (IReadOnlyList<ConversationCostSummary>)ok.Value!;
    }

    private static Conversation NewConversation(string title)
    {
        var ts = DateTimeOffset.UtcNow;
        return new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = Owner,
            Title = title,
            Status = ConversationStatus.Active,
            CreatedAt = ts,
            UpdatedAt = ts
        };
    }
}
