using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Persistence.Seam.Tests.Harness;

namespace BotNexus.Persistence.Seam.Tests.Sessions;

/// <summary>
/// Lost-update seam tests for the sessions aggregate (issue #3327, acceptance clause 2), driven
/// through the reusable <see cref="SeamGate"/>/<see cref="LostUpdateScenario{TAggregate}"/> harness
/// shipped by PR #3325.
/// </summary>
/// <remarks>
/// <para>
/// Every test runs the caller together with the REAL <c>SqliteSessionStore</c> against a real
/// database file. The regression class this program exists to prevent escaped precisely because
/// callers were tested against mocked stores and stores were tested in isolation: neither half
/// could observe the seam between them.
/// </para>
/// <para>
/// Ordering is a property of the tests, never of the machine: interleavings are established by
/// explicit <see cref="SeamGate"/> hand-offs or by the harness awaiting the concurrent mutation to
/// completion. Nothing here sleeps.
/// </para>
/// <para>
/// The sessions aggregate is unusual in that its session-row upsert (<c>SaveAsync</c>) is
/// deliberately unguarded — the pre-run write-ahead saves rely on it to create the row. History is
/// independently append-oriented and identity-reconciled (#3907). The protection is therefore
/// structural: narrow entry points added by #2132, history deltas, and the write fence added by
/// #1518. These tests characterise those seams and prove the fence suppresses a finalizer write
/// whose session was deleted, sealed or rebound mid-run.
/// </para>
/// </remarks>
public sealed class SessionLostUpdateSeamTests
{
    [Fact]
    public async Task FencedFinalizerSave_IsSuppressed_WhenACompetingResetSealsTheRowMidRun()
    {
        // The named non-vacuity target for #3327's sessions clause. A run captures its fence, a
        // competing reset seals the row, and the finalizer then tries to persist its completed
        // turn from an in-memory session that still believes it is Active. The unconditional
        // upsert behind SaveAsync would revert Sealed -> Active, so
        // the fence is the only thing standing between a reset session and its own resurrection.
        using var fixture = new SessionSeamStoreFixture();
        var seeded = await fixture.SeedAsync("s-fenced-vs-unfenced");

        var sealer = fixture.CreateStore();
        var sealResult = await sealer.TransitionStatusAsync(
            seeded.Session.SessionId,
            [SessionStatus.Active, SessionStatus.Suspended],
            SessionStatus.Sealed);
        sealResult.Outcome.ShouldBe(SessionMutationOutcome.Applied, "precondition: the reset must have sealed the row");

        seeded.Session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "late-turn" });
        var outcome = await seeded.Store.SaveAsync(seeded.Session, seeded.Fence);

        outcome.ShouldBe(SessionSaveOutcome.Rebound, "a seal landing mid-run must suppress the finalizer write");

        var committed = await fixture.CreateStore().GetAsync(seeded.Session.SessionId);
        committed.ShouldNotBeNull();
        committed.Status.ShouldBe(SessionStatus.Sealed, "the finalizer must not un-seal a reset session");
        committed.GetHistorySnapshot()
            .ShouldNotContain(e => e.Content == "late-turn", "the late turn must not be persisted onto a sealed session");
    }

    [Fact]
    public async Task UnfencedSave_FromTheSameStaleSnapshot_DoesUnsealTheRow()
    {
        // The contrast case that gives the test above its meaning. Identical setup, identical stale
        // snapshot - only the overload differs. The unfenced save is ACCEPTED and reverts the seal,
        // which is not a defect but the documented contract: SaveAsync is the create-or-update path
        // the pre-run write-ahead saves need. Pinning it here means "use the fenced overload in
        // finalizers" is backed by an executable demonstration of what happens if you do not,
        // rather than by a comment.
        using var fixture = new SessionSeamStoreFixture();
        var seeded = await fixture.SeedAsync("s-unfenced-contrast");

        (await fixture.CreateStore().TransitionStatusAsync(
            seeded.Session.SessionId,
            [SessionStatus.Active],
            SessionStatus.Sealed)).Outcome.ShouldBe(SessionMutationOutcome.Applied);

        seeded.Session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "late-turn" });
        await seeded.Store.SaveAsync(seeded.Session);

        var committed = await fixture.CreateStore().GetAsync(seeded.Session.SessionId);
        committed.ShouldNotBeNull();
        committed.Status.ShouldBe(
            SessionStatus.Active,
            "the unfenced overload is unconditional by contract; if this ever starts reporting "
            + "Sealed the fence has been folded into SaveAsync and the fenced overload's tests are "
            + "no longer proving anything distinct");
    }

    [Fact]
    public async Task FencedFinalizerSave_IsSuppressed_WhenTheRowIsDeletedMidRun()
    {
        // The resurrection vector: SaveAsync's INSERT … ON CONFLICT DO UPDATE recreates a row that
        // an operator or agent intentionally deleted. Verified through a COLD store so an
        // in-process cache cannot answer the assertion.
        using var fixture = new SessionSeamStoreFixture();
        var seeded = await fixture.SeedAsync("s-deleted-mid-run");

        await fixture.CreateStore().DeleteAsync(seeded.Session.SessionId);

        seeded.Session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "late-turn" });
        var outcome = await seeded.Store.SaveAsync(seeded.Session, seeded.Fence);

        outcome.ShouldBe(SessionSaveOutcome.Rebound);
        (await fixture.CreateStore().GetAsync(seeded.Session.SessionId))
            .ShouldBeNull("a deleted session must stay deleted; the finalizer must not resurrect it");
    }

    [Fact]
    public async Task FencedFinalizerSave_IsSuppressed_WhenTheSessionIsReboundToAnotherConversationMidRun()
    {
        // The third fence arm: the same session id now belongs to a different conversation, so the
        // finalizer would clobber a fresh binding with the identity it captured at run start.
        using var fixture = new SessionSeamStoreFixture();
        var seeded = await fixture.SeedAsync("s-rebound-mid-run");

        var rebindConversationId = await fixture.CreateConversationAsync(seeded.Session.AgentId);
        var rebinder = fixture.CreateStore();
        var rebound = await rebinder.GetAsync(seeded.Session.SessionId);
        rebound.ShouldNotBeNull();
        rebound.Session.ConversationId = rebindConversationId;
        await rebinder.SaveAsync(rebound);

        seeded.Session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "late-turn" });
        var outcome = await seeded.Store.SaveAsync(seeded.Session, seeded.Fence);

        outcome.ShouldBe(SessionSaveOutcome.Rebound);

        var committed = await fixture.CreateStore().GetAsync(seeded.Session.SessionId);
        committed.ShouldNotBeNull();
        committed.Session.ConversationId.ShouldBe(
            rebindConversationId,
            "the fresh binding must survive; the finalizer must not restore the run-start one");
    }

    [Fact]
    public async Task ParallelTurnFinalizers_GatedToOverlap_LeaveExactlyTheSurvivingRunsTurn()
    {
        // The parallel turn-finalizer interleaving named in #3327 clause 2, made deterministic.
        //
        // Two runs are in flight on the SAME session. Run A captures its fence and is then held at
        // a gate. Run B resets the session (seals it) and opens the gate, so A's finalizer provably
        // runs AFTER the reset committed rather than merely probably. A's fenced save must be
        // suppressed so A cannot un-seal the row. History is independently append-oriented, but
        // lifecycle resurrection remains forbidden and is what the fence protects here.
        using var fixture = new SessionSeamStoreFixture();
        var seeded = await fixture.SeedAsync("s-parallel-finalizers");
        var sessionId = seeded.Session.SessionId;

        // B's turn lands first and legitimately, through the narrow append path.
        (await fixture.CreateStore().AppendEntriesAsync(
            sessionId,
            [new SessionEntry { Role = MessageRole.Assistant, Content = "run-b-turn" }]))
            .Outcome.ShouldBe(SessionMutationOutcome.Applied);

        var runAHasCapturedFence = new SeamGate("run-a-captured-fence");
        var runBHasReset = new SeamGate("run-b-reset-committed");

        SessionSaveOutcome runAOutcome = default;

        var runA = Task.Run(async () =>
        {
            // A's in-memory session is the one seeded before B did anything - a genuinely stale
            // snapshot carrying only the seed turn.
            runAHasCapturedFence.Open();
            await runBHasReset.WaitAsync();

            seeded.Session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "run-a-turn" });
            runAOutcome = await seeded.Store.SaveAsync(seeded.Session, seeded.Fence);
        });

        var runB = Task.Run(async () =>
        {
            await runAHasCapturedFence.WaitAsync();
            var resetStore = fixture.CreateStore();
            (await resetStore.TransitionStatusAsync(sessionId, [SessionStatus.Active], SessionStatus.Sealed))
                .Outcome.ShouldBe(SessionMutationOutcome.Applied);
            runBHasReset.Open();
        });

        await Task.WhenAll(runA, runB);

        runAOutcome.ShouldBe(SessionSaveOutcome.Rebound, "run A's finalizer must observe the seal and skip");

        var committed = await fixture.CreateStore().GetAsync(sessionId);
        committed.ShouldNotBeNull();
        committed.Status.ShouldBe(SessionStatus.Sealed);

        var contents = committed.GetHistorySnapshot().Select(e => e.Content).ToArray();
        contents.ShouldContain("run-b-turn", "the surviving run's turn must not be erased by a suppressed finalizer");
        contents.ShouldNotContain("run-a-turn", "the suppressed finalizer must contribute nothing");
    }

    [Fact]
    public async Task MetadataPatch_SurvivesAConcurrentTranscriptAppend_BecauseTheyOwnDisjointColumns()
    {
        // #2132's core claim, exercised across a gated overlap rather than back-to-back: the append
        // writes session_history + updated_at, the patch writes the metadata column. Neither is in
        // the other's SET list, so both must land regardless of ordering. A store that implemented
        // either as a read-mutate-whole-aggregate-save would lose one of them here.
        using var fixture = new SessionSeamStoreFixture();
        var seeded = await fixture.SeedAsync("s-patch-vs-append");
        var sessionId = seeded.Session.SessionId;

        var appendCommitted = new SeamGate("append-committed");
        var patchCommitted = new SeamGate("patch-committed");

        var appender = Task.Run(async () =>
        {
            (await fixture.CreateStore().AppendEntriesAsync(
                sessionId,
                [new SessionEntry { Role = MessageRole.Assistant, Content = "appended-turn" }]))
                .Outcome.ShouldBe(SessionMutationOutcome.Applied);
            appendCommitted.Open();
            await patchCommitted.WaitAsync();

            // A second append AFTER the patch committed: the patch must survive this too.
            (await fixture.CreateStore().AppendEntriesAsync(
                sessionId,
                [new SessionEntry { Role = MessageRole.Assistant, Content = "second-turn" }]))
                .Outcome.ShouldBe(SessionMutationOutcome.Applied);
        });

        var patcher = Task.Run(async () =>
        {
            await appendCommitted.WaitAsync();
            (await fixture.CreateStore().PatchMetadataAsync(
                sessionId,
                new Dictionary<string, object?> { ["reviewed"] = "yes" }))
                .Outcome.ShouldBe(SessionMutationOutcome.Applied);
            patchCommitted.Open();
        });

        await Task.WhenAll(appender, patcher);

        var committed = await fixture.CreateStore().GetAsync(sessionId);
        committed.ShouldNotBeNull();
        committed.Metadata.ShouldContainKey("reviewed");
        committed.GetHistorySnapshot()
            .Select(e => e.Content)
            .ShouldBe(["seed-turn", "appended-turn", "second-turn"], ignoreOrder: false);
    }

    [Fact]
    public async Task ConcurrentMetadataPatches_Compose_RatherThanClobber()
    {
        // Metadata is classified Merge. Two producers writing DIFFERENT keys must both land; the
        // gates force their read-merge-write cycles to interleave so a whole-dictionary
        // replace-on-write implementation could not pass by accident of scheduling.
        using var fixture = new SessionSeamStoreFixture();
        var seeded = await fixture.SeedAsync("s-patch-vs-patch");
        var sessionId = seeded.Session.SessionId;

        var firstPatched = new SeamGate("first-patch-committed");
        var secondPatched = new SeamGate("second-patch-committed");

        var armA = Task.Run(async () =>
        {
            await fixture.CreateStore().PatchMetadataAsync(sessionId, new Dictionary<string, object?> { ["alpha"] = 1 });
            firstPatched.Open();
            await secondPatched.WaitAsync();
            // Re-patch alpha after B committed beta; beta must still be present afterwards.
            await fixture.CreateStore().PatchMetadataAsync(sessionId, new Dictionary<string, object?> { ["alpha"] = 2 });
        });

        var armB = Task.Run(async () =>
        {
            await firstPatched.WaitAsync();
            await fixture.CreateStore().PatchMetadataAsync(sessionId, new Dictionary<string, object?> { ["beta"] = 9 });
            secondPatched.Open();
        });

        await Task.WhenAll(armA, armB);

        var committed = await fixture.CreateStore().GetAsync(sessionId);
        committed.ShouldNotBeNull();
        committed.Metadata.ShouldContainKey("alpha");
        committed.Metadata.ShouldContainKey("beta");
    }

    [Fact]
    public async Task StatusTransition_FromAStaleExpectation_IsRefusedAndReportsTheAuthoritativeStatus()
    {
        // TransitionStatusAsync is the aggregate's compare-and-swap. A caller computing a suspend
        // from a snapshot another actor has already sealed must be refused loudly and told what it
        // lost to - a silent "last write wins" would revert a deliberate terminal state.
        using var fixture = new SessionSeamStoreFixture();
        var seeded = await fixture.SeedAsync("s-status-cas");
        var sessionId = seeded.Session.SessionId;

        var result = await new LostUpdateScenario<GatewaySession>()
            .ReadSnapshot(() => fixture.CreateStore().GetAsync(sessionId))
            .ThenConcurrently(async () =>
                (await fixture.CreateStore().TransitionStatusAsync(sessionId, [SessionStatus.Active], SessionStatus.Sealed))
                    .Outcome.ShouldBe(SessionMutationOutcome.Applied))
            .ThenStaleWrite(async snapshot =>
            {
                // The stale caller still believes the session is Active.
                snapshot.Status.ShouldBe(SessionStatus.Active);
                var refused = await fixture.CreateStore()
                    .TransitionStatusAsync(sessionId, [SessionStatus.Active], SessionStatus.Suspended);
                refused.Outcome.ShouldBe(SessionMutationOutcome.Conflict);
                refused.Status.ShouldBe(SessionStatus.Sealed, "the refusal must carry the authoritative status");
            })
            .VerifyBy(() => fixture.CreateStore().GetAsync(sessionId))
            .RunAsync();

        // The CAS reports its refusal in the RESULT rather than by throwing, so from the harness's
        // point of view the stale write completed - the invariant is asserted on what was committed.
        result.Outcome.ShouldBe(StaleWriteOutcome.Accepted);
        result.Committed.ShouldNotBeNull().Status.ShouldBe(SessionStatus.Sealed);
    }

    [Fact]
    public async Task AppendAgainstASealedSession_IsRefused_RatherThanRevivingTheTranscript()
    {
        // The append path's own conflict contract. A turn that arrives after a reset must not
        // extend a terminal session: the seal is a state a competing actor established
        // deliberately, and an append that "just worked" would make it meaningless.
        using var fixture = new SessionSeamStoreFixture();
        var seeded = await fixture.SeedAsync("s-append-vs-seal");
        var sessionId = seeded.Session.SessionId;

        (await fixture.CreateStore().TransitionStatusAsync(sessionId, [SessionStatus.Active], SessionStatus.Sealed))
            .Outcome.ShouldBe(SessionMutationOutcome.Applied);

        var refused = await fixture.CreateStore().AppendEntriesAsync(
            sessionId,
            [new SessionEntry { Role = MessageRole.Assistant, Content = "post-seal-turn" }]);

        refused.Outcome.ShouldBe(SessionMutationOutcome.Conflict);
        refused.AppendedCount.ShouldBe(0);

        var committed = await fixture.CreateStore().GetAsync(sessionId);
        committed.ShouldNotBeNull();
        committed.GetHistorySnapshot().ShouldNotContain(e => e.Content == "post-seal-turn");
    }

    [Fact]
    public async Task AppendAgainstAMissingSession_IsNotFound_RatherThanCreatingTheRow()
    {
        // The append path must never create. If it did, a turn belonging to a deleted session
        // would resurrect it by a different route than the one #1518's fence closes.
        using var fixture = new SessionSeamStoreFixture();
        var store = fixture.CreateStore();

        var result = await store.AppendEntriesAsync(
            SessionId.From("s-never-existed"),
            [new SessionEntry { Role = MessageRole.User, Content = "orphan" }]);

        result.Outcome.ShouldBe(SessionMutationOutcome.NotFound);
        (await fixture.CreateStore().GetAsync(SessionId.From("s-never-existed"))).ShouldBeNull();
    }

    [Fact]
    public async Task StaleUnfencedSave_PreservesAConcurrentAppend()
    {
        // #3907: the session-row upsert remains unconditional for write-ahead creation, but history
        // is no longer replaced from the stale snapshot. A concurrent narrow append is outside this
        // aggregate's deletion authority and must survive the later save.
        using var fixture = new SessionSeamStoreFixture();
        var seeded = await fixture.SeedAsync("s-save-preserves-append");
        var sessionId = seeded.Session.SessionId;

        var result = await new LostUpdateScenario<GatewaySession>()
            .ReadSnapshot(() => fixture.CreateStore().GetAsync(sessionId))
            .ThenConcurrently(async () =>
                (await fixture.CreateStore().AppendEntriesAsync(
                    sessionId,
                    [new SessionEntry { Role = MessageRole.Assistant, Content = "concurrent-turn" }]))
                    .Outcome.ShouldBe(SessionMutationOutcome.Applied))
            .ThenStaleWrite(snapshot => fixture.CreateStore().SaveAsync(snapshot))
            .VerifyBy(() => fixture.CreateStore().GetAsync(sessionId))
            .RunAsync();

        result.Outcome.ShouldBe(StaleWriteOutcome.Accepted, "the unfenced save is unconditional by contract");

        var committed = result.Committed.ShouldNotBeNull();
        committed.GetHistorySnapshot()
            .ShouldContain(
                e => e.Content == "concurrent-turn",
                "append-oriented persistence must not erase a row committed after this aggregate snapshot");
    }
}
