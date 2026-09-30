using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Persistence.Seam.Tests.Harness;

namespace BotNexus.Persistence.Seam.Tests.Cron;

/// <summary>
/// Deterministic lost-update coverage for the real cron SQLite seam (issue #3327, clause 4).
/// </summary>
public sealed class CronLostUpdateSeamTests
{
    [Fact]
    public async Task UpdateDefinitionAsync_PreservesRunBookkeepingCommittedAfterItsSnapshot()
    {
        using var fixture = new CronSeamStoreFixture();
        var job = await fixture.SeedAsync("cron-definition-vs-run");
        var finalizedAt = new DateTimeOffset(2026, 8, 12, 10, 30, 0, TimeSpan.Zero);

        var result = await new LostUpdateScenario<CronJob>()
            .ReadSnapshot(() => fixture.CreateStore().GetAsync(job.Id))
            .ThenConcurrently(() => fixture.CreateStore().RecordRunFinalizationAsync(
                job.Id, finalizedAt, CronRunStatus.Error, "concurrent failure"))
            .ThenStaleWrite(async snapshot =>
            {
                var saved = await fixture.CreateStore().UpdateDefinitionAsync(snapshot with { Name = "renamed" });
                saved.ShouldNotBeNull();
            })
            .VerifyBy(() => fixture.CreateStore().GetAsync(job.Id))
            .RunAsync();

        result.Outcome.ShouldBe(StaleWriteOutcome.Accepted);
        var committed = result.Committed.ShouldNotBeNull();
        committed.Name.ShouldBe("renamed");
        committed.LastRunAt.ShouldBe(finalizedAt);
        committed.LastRunStatus.ShouldBe(CronRunStatus.Error);
        committed.LastRunError.ShouldBe("concurrent failure");
    }

    [Fact]
    public async Task UpdateDefinitionAsync_PreservesConversationCasCommittedAfterItsSnapshot()
    {
        using var fixture = new CronSeamStoreFixture();
        var job = await fixture.SeedAsync("cron-definition-vs-conversation");
        var conversationId = ConversationId.From("cron-conversation-winner");

        var result = await new LostUpdateScenario<CronJob>()
            .ReadSnapshot(() => fixture.CreateStore().GetAsync(job.Id))
            .ThenConcurrently(async () =>
            {
                var winner = await fixture.CreateStore().TrySetConversationIdAsync(job.Id, conversationId);
                winner.ShouldBe(conversationId);
            })
            .ThenStaleWrite(async snapshot =>
            {
                snapshot.ConversationId.ShouldBeNull("the definition writer must carry a genuinely stale pin");
                var saved = await fixture.CreateStore().UpdateDefinitionAsync(snapshot with { Enabled = false });
                saved.ShouldNotBeNull();
            })
            .VerifyBy(() => fixture.CreateStore().GetAsync(job.Id))
            .RunAsync();

        result.Outcome.ShouldBe(StaleWriteOutcome.Accepted);
        var committed = result.Committed.ShouldNotBeNull();
        committed.Enabled.ShouldBeFalse();
        committed.ConversationId.ShouldBe(conversationId);
    }
}
