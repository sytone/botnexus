using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Persistence.Seam.Tests.Harness;

namespace BotNexus.Persistence.Seam.Tests.Conversations;

/// <summary>
/// Lost-update seam tests for the conversations aggregate (issue #2130, acceptance clause 3),
/// driven through the reusable <see cref="LostUpdateScenario{TAggregate}"/> harness.
/// </summary>
/// <remarks>
/// <para>
/// Every test runs the caller together with the REAL <c>SqliteConversationStore</c> against a real
/// database file. The regression this program exists to prevent escaped precisely because the
/// controller was tested with a mocked store and the store was tested in isolation: neither half
/// could observe the seam between them.
/// </para>
/// <para>
/// Ordering is established by awaiting the interleaved mutation to completion inside the harness,
/// or by explicit <see cref="SeamGate"/> hand-offs where two arms must genuinely overlap. Nothing
/// here sleeps.
/// </para>
/// <para>
/// These cases target the seams NOT already covered by the store's own #2131 concurrency suite
/// (pin, canvas HTML, todo, metadata patch, binding add): the active-session field, the canvas
/// STATE side table, the participant merge, and the pending-prompt field.
/// </para>
/// </remarks>
public sealed class ConversationLostUpdateSeamTests
{
    [Fact]
    public async Task StaleSave_CannotResurrectAnActiveSession_ClearedByAConcurrentArchive()
    {
        // ActiveSessionId is caller-owned on SaveAsync (it is in the DO UPDATE SET list) AND
        // store-owned on ArchiveAsync (which nulls it). That overlap is the lost-update hazard:
        // a snapshot read while a session was live still carries that session id, so an unguarded
        // save would re-attach a session to an archived conversation - a state the store's own
        // lifecycle validation considers illegal.
        using var fixture = new SeamStoreFixture();
        var writer = fixture.CreateStore();
        var id = await SeedAsync(writer, c => c.ActiveSessionId = SessionId.From("session-live"));

        var result = await new LostUpdateScenario<Conversation>()
            .ReadSnapshot(() => writer.GetAsync(id))
            .ThenConcurrently(() => writer.ArchiveAsync(id))
            .ThenStaleWrite(snapshot =>
            {
                snapshot.Title = "renamed while archiving";
                return writer.SaveAsync(snapshot);
            })
            .VerifyBy(() => fixture.CreateStore().GetAsync(id))
            .RunAsync();

        result.Outcome.ShouldBe(StaleWriteOutcome.Rejected);
        result.Rejection.ShouldBeOfType<ConversationConcurrencyException>();

        var committed = result.Committed.ShouldNotBeNull();
        committed.Status.ShouldBe(ConversationStatus.Archived);
        committed.ActiveSessionId.ShouldBeNull();
        committed.Title.ShouldBe("seed title");
    }

    [Fact]
    public async Task StaleSave_CannotRevertAConcurrentPendingPromptWrite()
    {
        // PendingAskUserJson is a durable ask_user checkpoint. A concurrent editor saving an
        // unrelated field from a pre-prompt snapshot would erase the pending prompt and strand
        // the user's question.
        using var fixture = new SeamStoreFixture();
        var writer = fixture.CreateStore();
        var id = await SeedAsync(writer);

        var result = await new LostUpdateScenario<Conversation>()
            .ReadSnapshot(() => writer.GetAsync(id))
            .ThenConcurrently(async () =>
            {
                var winner = await fixture.CreateStore().GetAsync(id);
                winner!.PendingAskUserJson = """{"prompt":"approve?"}""";
                await fixture.CreateStore().SaveAsync(winner);
            })
            .ThenStaleWrite(snapshot =>
            {
                snapshot.Purpose = "unrelated edit";
                return writer.SaveAsync(snapshot);
            })
            .VerifyBy(() => fixture.CreateStore().GetAsync(id))
            .RunAsync();

        result.Outcome.ShouldBe(StaleWriteOutcome.Rejected);

        var committed = result.Committed.ShouldNotBeNull();
        committed.PendingAskUserJson.ShouldBe("""{"prompt":"approve?"}""");
        committed.Purpose.ShouldBeNull();
    }

    [Fact]
    public async Task ParticipantMerge_SurvivesAConcurrentFullSave_BecauseSaveDoesNotOwnParticipants()
    {
        // Participants are classified Merge and are NOT in SaveAsync's write set at all. That is a
        // stronger guarantee than the CAS: the save is ACCEPTED here (its revision is current) and
        // the participant added afterwards must still be present, because a full save has no
        // business rewriting the participant roster.
        using var fixture = new SeamStoreFixture();
        var writer = fixture.CreateStore();
        var id = await SeedAsync(writer);

        await writer.AddParticipantsAsync(id, [Participant("agent-one", "initiator")]);

        var snapshot = await writer.GetAsync(id);
        snapshot.ShouldNotBeNull();
        snapshot.Participants.Count.ShouldBe(1);

        // A second producer joins AFTER the snapshot was taken.
        await fixture.CreateStore().AddParticipantsAsync(id, [Participant("agent-two", "peer")]);

        // The snapshot still holds a one-participant roster. Saving it must not shrink the set.
        // AddParticipantsAsync deliberately does not bump the aggregate revision (it writes a
        // different table), so this save is legitimately accepted - which is exactly why the
        // roster has to be protected structurally rather than by the CAS.
        snapshot.Title = "renamed";
        await writer.SaveAsync(snapshot);

        var committed = await fixture.CreateStore().GetAsync(id);
        committed.ShouldNotBeNull();
        committed.Title.ShouldBe("renamed");
        committed.Participants
            .Select(p => p.CitizenId.Value)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ShouldBe(["agent-one", "agent-two"]);
    }

    [Fact]
    public async Task CanvasStateKey_SurvivesAConcurrentFullSave_BecauseItLivesInASideTable()
    {
        // Canvas STATE (the key/value side table written by the canvasState bridge) is distinct
        // from canvas HTML (a column SaveAsync replaces). A full save interleaved with a state
        // write must not drop the key: losing it silently discards data a rendered canvas wrote.
        using var fixture = new SeamStoreFixture();
        var writer = fixture.CreateStore();
        var id = await SeedAsync(writer);

        var snapshot = await writer.GetAsync(id);
        snapshot.ShouldNotBeNull();

        (await fixture.CreateStore().SetCanvasStateKeyAsync(id, "answers", JsonDocument.Parse("""{"q1":"yes"}""").RootElement))
            .ShouldBeTrue();

        snapshot.Title = "renamed after canvas write";
        await writer.SaveAsync(snapshot);

        var state = await fixture.CreateStore().GetCanvasStateAsync(id);
        state.ShouldNotBeNull();
        state.ShouldContainKey("answers");
        state["answers"].GetProperty("q1").GetString().ShouldBe("yes");
    }

    [Fact]
    public async Task ConcurrentCanvasStateKeys_BothSurvive_WhenTheWritesAreGatedToOverlap()
    {
        // Two canvas producers writing DIFFERENT keys must both land. The gates force the reads
        // and writes to interleave rather than run back-to-back, so a whole-dictionary
        // replace-on-write implementation could not pass by accident of scheduling.
        using var fixture = new SeamStoreFixture();
        var writer = fixture.CreateStore();
        var id = await SeedAsync(writer);

        var firstHasWritten = new SeamGate("first-key-written");
        var secondHasWritten = new SeamGate("second-key-written");

        var armA = Task.Run(async () =>
        {
            await fixture.CreateStore().SetCanvasStateKeyAsync(id, "alpha", Json("""1"""));
            firstHasWritten.Open();
            await secondHasWritten.WaitAsync();
            // Re-write alpha after B committed beta; beta must still be there afterwards.
            await fixture.CreateStore().SetCanvasStateKeyAsync(id, "alpha", Json("""2"""));
        });

        var armB = Task.Run(async () =>
        {
            await firstHasWritten.WaitAsync();
            await fixture.CreateStore().SetCanvasStateKeyAsync(id, "beta", Json("""9"""));
            secondHasWritten.Open();
        });

        await Task.WhenAll(armA, armB);

        var state = await fixture.CreateStore().GetCanvasStateAsync(id);
        state.ShouldNotBeNull();
        state.Count.ShouldBe(2);
        state["alpha"].GetInt32().ShouldBe(2);
        state["beta"].GetInt32().ShouldBe(9);
    }

    [Fact]
    public async Task TwoArmsGatedToReadTheSameRevision_ProduceExactlyOneWinner_AndTheLoserIsToldSo()
    {
        // The canonical silent-loss shape, made deterministic: both arms are held at the gate
        // until BOTH have read, so they provably share a revision. Exactly one save may succeed
        // and the other must be refused loudly - "last write wins" would pass a naive test that
        // merely checked the row still exists.
        using var fixture = new SeamStoreFixture();
        var writer = fixture.CreateStore();
        var id = await SeedAsync(writer);

        var armARead = new SeamGate("arm-a-read");
        var armBRead = new SeamGate("arm-b-read");

        async Task<Exception?> SaveFrom(SeamGate mine, SeamGate theirs, Action<Conversation> edit)
        {
            // Both arms deliberately share ONE store instance. The store serialises writes to a
            // conversation with an in-process striped lock, so sharing it isolates the guarantee
            // under test (the revision compare-and-swap) from SQLite's own file-level write lock -
            // otherwise a loser could surface as "database is locked" and the test would be
            // asserting the wrong mechanism.
            var snapshot = await writer.GetAsync(id);
            snapshot.ShouldNotBeNull();
            await mine.OpenThenWaitAsync(theirs);
            edit(snapshot);
            try
            {
                await writer.SaveAsync(snapshot);
                return null;
            }
            catch (ConversationConcurrencyException ex)
            {
                return ex;
            }
        }

        var a = SaveFrom(armARead, armBRead, c => c.Title = "from-A");
        var b = SaveFrom(armBRead, armARead, c => c.Purpose = "from-B");
        var outcomes = await Task.WhenAll(a, b);

        outcomes.Count(o => o is null).ShouldBe(1, "exactly one of two same-revision saves may commit");
        var loser = outcomes.Single(o => o is not null).ShouldBeOfType<ConversationConcurrencyException>();
        loser.ExpectedVersion.ShouldBeLessThan(loser.ActualVersion);

        // And the winner's write is intact rather than half-applied.
        var committed = await fixture.CreateStore().GetAsync(id);
        committed.ShouldNotBeNull();
        var winnerWroteTitle = committed.Title == "from-A";
        var winnerWrotePurpose = committed.Purpose == "from-B";
        (winnerWroteTitle ^ winnerWrotePurpose).ShouldBeTrue(
            "exactly one arm's edit must be committed; seeing both would mean the loser's write "
            + "was silently merged, and seeing neither would mean both were dropped");
    }

    [Fact]
    public async Task ReReadRetry_AfterRejection_PreservesBothTheRetriedEditAndTheConcurrentChange()
    {
        // The prescribed recovery documented in ConversationConcurrencyException. A seam program
        // that only proves writes get REFUSED is incomplete: it must also prove the refusal is
        // recoverable, otherwise the guard would be indistinguishable from an outage.
        using var fixture = new SeamStoreFixture();
        var writer = fixture.CreateStore();
        var id = await SeedAsync(writer);

        var result = await new LostUpdateScenario<Conversation>()
            .ReadSnapshot(() => writer.GetAsync(id))
            .ThenConcurrently(() => writer.PinAsync(id, pin: true))
            .ThenStaleWrite(snapshot =>
            {
                snapshot.Instructions = "be terse";
                return writer.SaveAsync(snapshot);
            })
            .VerifyBy(() => fixture.CreateStore().GetAsync(id))
            .RunAsync();

        result.Outcome.ShouldBe(StaleWriteOutcome.Rejected);

        // Re-read, re-apply the same intent, retry.
        var fresh = await writer.GetAsync(id);
        fresh.ShouldNotBeNull();
        fresh.Instructions = "be terse";
        await writer.SaveAsync(fresh);

        var committed = await fixture.CreateStore().GetAsync(id);
        committed.ShouldNotBeNull();
        committed.Instructions.ShouldBe("be terse");
        committed.IsPinned.ShouldBeTrue("the concurrent pin must survive the retry, not be clobbered by it");
    }

    [Fact]
    public async Task Harness_FailsLoudly_WhenAnInterleavingCannotComplete()
    {
        // Non-vacuity guard for the harness itself: a gate nobody opens must surface as a named
        // wiring error, not as a silent pass or an indefinite hang. Without this, a seam test
        // whose second arm never ran could look green.
        var neverOpened = new SeamGate("never-opened");

        var ex = await Should.ThrowAsync<SeamDeadlockException>(
            () => neverOpened.WaitAsync(TimeSpan.FromMilliseconds(200)));

        ex.Message.ShouldContain("never-opened");
    }

    [Fact]
    public async Task Harness_RefusesToRunAgainstAMissingAggregate()
    {
        // The other vacuity trap: if the fixture never created the row, every "the field survived"
        // assertion would be comparing nulls. The harness refuses rather than reporting green.
        using var fixture = new SeamStoreFixture();
        var store = fixture.CreateStore();

        var scenario = new LostUpdateScenario<Conversation>()
            .ReadSnapshot(() => store.GetAsync(ConversationId.Create()))
            .ThenConcurrently(() => Task.CompletedTask)
            .ThenStaleWrite(_ => Task.CompletedTask)
            .VerifyBy(() => Task.FromResult<Conversation?>(null));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => scenario.RunAsync());
        ex.Message.ShouldContain("vacuously");
    }

    private static async Task<ConversationId> SeedAsync(
        SqliteConversationStore store,
        Action<Conversation>? customise = null)
    {
        var conversation = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = AgentId.From("seam-agent"),
            Title = "seed title",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        customise?.Invoke(conversation);
        await store.CreateAsync(conversation);
        return conversation.ConversationId;
    }

    private static SessionParticipant Participant(string agentId, string role)
        => new() { CitizenId = CitizenId.Of(AgentId.From(agentId)), Role = role };

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();
}
