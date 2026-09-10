using BotNexus.Domain.Primitives;
using BotNexus.Memory.Learning;
using BotNexus.Memory.Models;
using BotNexus.Memory.Tests.TestInfrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Memory.Tests.Learning;

/// <summary>
/// Covers session-end learning extraction. The pipeline it drives was built and tested long before
/// it had a caller other than the nightly dreaming cron, and that caller keeps only the items which
/// route to a shared store — so an agent's own distilled knowledge was extracted and then thrown
/// away. These tests pin that it is now persisted, that a re-index does not duplicate it, and that
/// distillation cannot launder a row's provenance.
/// </summary>
public sealed class SessionLearningExtractorTests
{
    // Over the classifier's 100-character durability floor, and carrying decision/pattern markers
    // ("decided", "always", "convention") so it classifies as durable rather than transient.
    private const string DurableUser = "How should we handle provider retries in the gateway?";
    private const string DurableAssistant =
        "We decided to always use exponential backoff with a 30 second ceiling for provider calls, " +
        "and the convention is to log every retry at debug level so the pattern stays visible in " +
        "production traces.";

    private static MemoryEntry ConversationRow(
        string id, int turnIndex, string sessionId, string? userId = null,
        string provenance = MemoryProvenance.User,
        string user = DurableUser, string assistant = DurableAssistant)
        => MemoryStoreTestContext.CreateEntry(
            id, "agent-a", TranscriptTurnFormat.Encode(user, assistant),
            sourceType: "conversation", sessionId: sessionId, turnIndex: turnIndex) with
        {
            Provenance = provenance,
            UserId = userId
        };

    [Fact]
    public async Task Extract_DurableExchange_WritesALearningRow()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(ConversationRow("c1", 0, "s1"));

        var written = await SessionLearningExtractor.ExtractAsync(
            context.Store, AgentId.From("agent-a"), SessionId.From("s1"), NullLogger.Instance);

        written.ShouldBe(1);

        var rows = await context.Store.GetBySessionAsync("s1", 50);
        var learning = rows.Where(r => r.SourceType == SessionLearningExtractor.LearningSourceType).ToList();
        learning.ShouldHaveSingleItem();
        learning[0].TurnIndex.ShouldBe(0);
        learning[0].OriginSessionId.ShouldBe("s1");
    }

    [Fact]
    public async Task Extract_RunTwice_IsIdempotent()
    {
        // The indexer can run more than once for the same session — a reconnect, a replayed close
        // event. Without the turn-index guard, every re-index would duplicate the distillation.
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(ConversationRow("c1", 0, "s1"));

        var first = await SessionLearningExtractor.ExtractAsync(
            context.Store, AgentId.From("agent-a"), SessionId.From("s1"), NullLogger.Instance);
        var second = await SessionLearningExtractor.ExtractAsync(
            context.Store, AgentId.From("agent-a"), SessionId.From("s1"), NullLogger.Instance);

        first.ShouldBe(1);
        second.ShouldBe(0);

        var rows = await context.Store.GetBySessionAsync("s1", 50);
        rows.Count(r => r.SourceType == SessionLearningExtractor.LearningSourceType).ShouldBe(1);
    }

    [Fact]
    public async Task Extract_TransientExchange_WritesNothing()
    {
        // The sad path: if greetings distilled into learning rows the store would fill with noise
        // and the feature would be worse than not having it.
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(
            ConversationRow("c1", 0, "s1", user: "hi there", assistant: "Hello! How can I help?"));

        var written = await SessionLearningExtractor.ExtractAsync(
            context.Store, AgentId.From("agent-a"), SessionId.From("s1"), NullLogger.Instance);

        written.ShouldBe(0);
    }

    [Fact]
    public async Task Extract_FromQuarantinedRow_DoesNotProduceFirstPartyKnowledge()
    {
        // The laundering path this must not open: a row whose content came from an untrusted
        // third party must not become first-party agent knowledge by passing through distillation.
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(
            ConversationRow("c1", 0, "s1", provenance: MemoryProvenance.ExternalUntrusted));

        await SessionLearningExtractor.ExtractAsync(context.Store, AgentId.From("agent-a"), SessionId.From("s1"), NullLogger.Instance);

        var learning = (await context.Store.GetBySessionAsync("s1", 50))
            .Single(r => r.SourceType == SessionLearningExtractor.LearningSourceType);

        MemoryProvenance.IsFirstParty(learning.Provenance).ShouldBeFalse();
        learning.NormalizedProvenance.ShouldNotBe(MemoryProvenance.Agent);
    }

    [Fact]
    public async Task Extract_InheritsUserId_WhenTheSessionHasOneContributor()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(ConversationRow("c1", 0, "s1", userId: "person-a"));

        await SessionLearningExtractor.ExtractAsync(context.Store, AgentId.From("agent-a"), SessionId.From("s1"), NullLogger.Instance);

        var learning = (await context.Store.GetBySessionAsync("s1", 50))
            .Single(r => r.SourceType == SessionLearningExtractor.LearningSourceType);
        learning.UserId.ShouldBe("person-a");
    }

    [Fact]
    public async Task Extract_LeavesUserIdNull_WhenContributorsDisagree()
    {
        // Attribution has to be provably right or absent. Picking one of two speakers would put
        // words in someone's mouth, which is worse than recording nothing.
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(ConversationRow("c1", 0, "s1", userId: "person-a"));
        await context.Store.InsertAsync(ConversationRow("c2", 2, "s1", userId: "person-b"));

        await SessionLearningExtractor.ExtractAsync(context.Store, AgentId.From("agent-a"), SessionId.From("s1"), NullLogger.Instance);

        var learning = (await context.Store.GetBySessionAsync("s1", 50))
            .Where(r => r.SourceType == SessionLearningExtractor.LearningSourceType)
            .ToList();
        learning.ShouldNotBeEmpty();
        learning.ShouldAllBe(r => r.UserId == null);
    }

    [Fact]
    public async Task Extract_EmptySession_IsANoOp()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();

        var written = await SessionLearningExtractor.ExtractAsync(
            context.Store, AgentId.From("agent-a"), SessionId.From("no-such-session"), NullLogger.Instance);

        written.ShouldBe(0);
    }

    [Fact]
    public async Task Extract_DoesNotDistilItsOwnLearningRows()
    {
        // Learning rows are not source_type "conversation", so the pipeline skips them. Without
        // that, each pass would distil the previous pass's output and the store would grow on
        // every session close forever.
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(ConversationRow("c1", 0, "s1"));

        await SessionLearningExtractor.ExtractAsync(context.Store, AgentId.From("agent-a"), SessionId.From("s1"), NullLogger.Instance);
        await SessionLearningExtractor.ExtractAsync(context.Store, AgentId.From("agent-a"), SessionId.From("s1"), NullLogger.Instance);
        await SessionLearningExtractor.ExtractAsync(context.Store, AgentId.From("agent-a"), SessionId.From("s1"), NullLogger.Instance);

        var rows = await context.Store.GetBySessionAsync("s1", 50);
        rows.Count(r => r.SourceType == SessionLearningExtractor.LearningSourceType).ShouldBe(1);
    }
}
