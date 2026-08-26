using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Concurrency;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// #3517 AC2: the audited <see cref="SqliteConversationStore.ArchiveAsync(ConversationId, string, string?, string, CancellationToken)"/>
/// overload must fail within a bounded time, with an exception that names the lock contention,
/// when the conversation's write stripe is held by somebody else.
/// </summary>
/// <remarks>
/// <para>
/// This overload is the JANITOR path - cron one-shot removal, duplicate cleanup - and its callers
/// pass <see cref="CancellationToken.None"/>. Before this fix the acquire had no bound of its own,
/// so a stripe held by a wedged cron run made the archive an unbounded wait, and the incident's
/// visible failure was a bare <c>TaskCanceledException</c> from inside
/// <c>SemaphoreSlim.WaitAsync</c> - which says "cancelled" when nothing had been cancelled.
/// </para>
/// <para>
/// The contention is created by holding the stripe explicitly and never releasing it, so the
/// outcome is deterministic on every run rather than depending on two operations overlapping.
/// </para>
/// </remarks>
public sealed class SqliteConversationStoreArchiveLockTimeoutTests
{
    [Fact]
    public async Task AuditedArchive_WhenTheStripeIsHeld_FailsWithAStripeLockTimeout_NotABareCancellation()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        store.ArchiveLockTimeout = TimeSpan.FromMilliseconds(100);

        var conversation = NewConversation("agent-a");
        await store.CreateAsync(conversation);
        var id = conversation.ConversationId;

        // Hold the very stripe the archive needs, and never release it - the wedged-run shape.
        using var held = await store.AcquireConversationLockForTestAsync(id.Value);

        var ex = await Should.ThrowAsync<StripeLockTimeoutException>(
            async () => await store.ArchiveAsync(id, "cron-delete-after-run", "job-1", "system", CancellationToken.None));

        ex.Key.ShouldBe(id.Value, "an operator must be able to see WHICH conversation was contended");
        ex.ShouldBeAssignableTo<TimeoutException>();
        ex.ShouldNotBeAssignableTo<OperationCanceledException>(
            "the reported signature was a TaskCanceledException raised with CancellationToken.None, which is precisely what made it undiagnosable");
    }

    [Fact]
    public async Task AuditedArchive_OnAnUncontendedConversation_StillArchives()
    {
        // Non-vacuity: the bound must not be the thing that fails a normal archive. Without this,
        // an implementation that simply always threw would pass the test above.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        store.ArchiveLockTimeout = TimeSpan.FromMilliseconds(100);

        var conversation = NewConversation("agent-a");
        await store.CreateAsync(conversation);

        await store.ArchiveAsync(conversation.ConversationId, "cron-delete-after-run", "job-1", "system", CancellationToken.None);

        (await store.GetAsync(conversation.ConversationId))!.Status.ShouldBe(ConversationStatus.Archived);
    }

    [Fact]
    public async Task AuditedArchive_SucceedsOnceTheStripeIsReleased_WithinTheBound()
    {
        // The bound is a deadline, not a refusal to queue: a holder that lets go in time must hand
        // the archive through normally.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        store.ArchiveLockTimeout = TimeSpan.FromSeconds(30);

        var conversation = NewConversation("agent-a");
        await store.CreateAsync(conversation);
        var id = conversation.ConversationId;

        var held = await store.AcquireConversationLockForTestAsync(id.Value);
        var archive = store.ArchiveAsync(id, "cron-delete-after-run", "job-1", "system", CancellationToken.None);
        archive.IsCompleted.ShouldBeFalse("the stripe is held, so the archive must be queued behind it");

        held.Dispose();

        await archive.WaitAsync(TimeSpan.FromSeconds(10));
        (await store.GetAsync(id))!.Status.ShouldBe(ConversationStatus.Archived);
    }

    [Fact]
    public void TheDefaultBound_IsPositiveAndFinite()
    {
        // The whole defect is an UNBOUNDED wait. A default of zero or infinity would reintroduce it
        // for every caller that does not opt in.
        SqliteConversationStore.DefaultArchiveLockTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
        SqliteConversationStore.DefaultArchiveLockTimeout.ShouldNotBe(Timeout.InfiniteTimeSpan);
    }

    private static Conversation NewConversation(string agentId) => new()
    {
        ConversationId = ConversationId.From($"c_{Guid.NewGuid():N}"),
        AgentId = AgentId.From(agentId),
        Title = "archive lock timeout",
        Status = ConversationStatus.Active
    };
}
