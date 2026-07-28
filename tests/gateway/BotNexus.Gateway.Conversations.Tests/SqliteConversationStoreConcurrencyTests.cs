using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Stale-save / optimistic-concurrency guarantees for <see cref="SqliteConversationStore"/>
/// (issue #2131).
/// </summary>
/// <remarks>
/// <para>
/// Every test here runs against a REAL SQLite database file (<see cref="StoreFixture"/>), not an
/// in-memory fake, because the guarantee under test lives in the SQL - the conditional
/// <c>ON CONFLICT ... WHERE version = $expectedVersion</c> upsert - and an in-memory double could
/// not regress it.
/// </para>
/// <para>
/// Interleavings are forced with explicit <see cref="TaskCompletionSource"/> gates rather than
/// sleeps or thread races, so the "read, then someone else commits, then save" ordering is
/// deterministic on every run.
/// </para>
/// </remarks>
public sealed class SqliteConversationStoreConcurrencyTests
{
    [Fact]
    public async Task StaleTitleSave_CannotClearAConcurrentPin()
    {
        // Acceptance 1: reader takes a snapshot, a pin commits, then the reader saves a
        // title change built from the pre-pin snapshot. The pin must survive.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = NewConversation("agent-a", "original title");
        await store.CreateAsync(conversation);
        var id = conversation.ConversationId;

        // 1. Editor reads the aggregate (IsPinned == false at this revision).
        var snapshot = await store.GetAsync(id);
        snapshot.ShouldNotBeNull();
        snapshot!.IsPinned.ShouldBeFalse();

        // 2. A narrow mutation commits AFTER that read.
        await store.PinAsync(id, pin: true);

        // 3. The editor mutates only the title and saves its now-stale aggregate. The stale
        //    aggregate still carries IsPinned == false, so an unguarded full-row upsert would
        //    silently unpin the conversation.
        snapshot.Title = "edited title";
        var conflict = await Should.ThrowAsync<ConversationConcurrencyException>(
            () => store.SaveAsync(snapshot));
        conflict.ConversationId.ShouldBe(id.Value);

        // The observable that a broken implementation moves: the pin is still set.
        var reloaded = await fixture.CreateStore().GetAsync(id);
        reloaded.ShouldNotBeNull();
        reloaded!.IsPinned.ShouldBeTrue();
        reloaded.PinnedAt.ShouldNotBeNull();
        // And the stale write was rejected outright rather than half-applied.
        reloaded.Title.ShouldBe("original title");
    }

    [Fact]
    public async Task StaleInstructionsSave_CannotOverwriteAConcurrentCanvasAndTodoWrite()
    {
        // Acceptance 2: a stale canvas/todo/instructions-shaped edit must not roll back
        // unrelated fields committed after the read.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = NewConversation("agent-a", "conv");
        conversation.CanvasHtml = "<p>v1</p>";
        conversation.TodoJson = """[{"id":1}]""";
        await store.CreateAsync(conversation);
        var id = conversation.ConversationId;

        var snapshot = await store.GetAsync(id);
        snapshot.ShouldNotBeNull();

        // Winner commits new canvas + todo + model override through a fresh aggregate.
        var winner = await store.GetAsync(id);
        winner!.CanvasHtml = "<p>v2-winner</p>";
        winner.TodoJson = """[{"id":2}]""";
        winner.ModelOverride = "opus-5";
        await store.SaveAsync(winner);

        // Loser edits only instructions from the pre-winner snapshot. Its aggregate still holds
        // the v1 canvas/todo and a null override, so an unguarded upsert would revert all three.
        snapshot!.Instructions = "be terse";
        await Should.ThrowAsync<ConversationConcurrencyException>(() => store.SaveAsync(snapshot));

        var reloaded = await fixture.CreateStore().GetAsync(id);
        reloaded.ShouldNotBeNull();
        reloaded!.CanvasHtml.ShouldBe("<p>v2-winner</p>");
        reloaded.TodoJson.ShouldBe("""[{"id":2}]""");
        reloaded.ModelOverride.ShouldBe("opus-5");
        reloaded.Instructions.ShouldBeNull();
    }

    [Fact]
    public async Task StaleSave_CannotRevertANarrowMetadataPatch()
    {
        // Acceptance 2 (patch seam): PatchMetadataAsync bumps the revision, so a whole-aggregate
        // save built before the patch cannot restore the old title.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = NewConversation("agent-a", "before");
        await store.CreateAsync(conversation);
        var id = conversation.ConversationId;

        var snapshot = await store.GetAsync(id);
        snapshot.ShouldNotBeNull();

        await store.PatchMetadataAsync(id, new ConversationMetadataPatch { Title = FieldUpdate<string>.Set("patched") });

        snapshot!.Purpose = "unrelated edit";
        await Should.ThrowAsync<ConversationConcurrencyException>(() => store.SaveAsync(snapshot));

        var reloaded = await fixture.CreateStore().GetAsync(id);
        reloaded!.Title.ShouldBe("patched");
        reloaded.Purpose.ShouldBeNull();
    }

    [Fact]
    public async Task StaleSave_CannotRevertAConcurrentBindingAdd()
    {
        // A whole-aggregate save deletes and recreates the binding set from its snapshot, so an
        // interleaved AddBindingAsync would otherwise vanish.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = NewConversation("agent-a", "conv");
        await store.CreateAsync(conversation);
        var id = conversation.ConversationId;

        var snapshot = await store.GetAsync(id);
        snapshot!.ChannelBindings.Count.ShouldBe(0);

        (await store.AddBindingAsync(id, NewBinding("telegram", "999"))).ShouldBeTrue();

        snapshot.Title = "renamed";
        await Should.ThrowAsync<ConversationConcurrencyException>(() => store.SaveAsync(snapshot));

        var reloaded = await fixture.CreateStore().GetAsync(id);
        reloaded!.ChannelBindings.Count.ShouldBe(1);
        reloaded.ChannelBindings[0].ChannelAddress.ShouldBe(ChannelAddress.From("999"));
    }

    [Fact]
    public async Task TwoConcurrentSaves_GatedDeterministically_OnlyOneWins_AndTheLoserIsToldSo()
    {
        // Acceptance 3: silent loss is forbidden. Both writers read the SAME revision; the second
        // save to reach the store must surface a conflict rather than last-write-wins.
        // Gates make the "both read before either writes" interleaving deterministic.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = NewConversation("agent-a", "base");
        await store.CreateAsync(conversation);
        var id = conversation.ConversationId;

        var bothHaveRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSaveDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writerA = Task.Run(async () =>
        {
            var a = await store.GetAsync(id);
            a!.Title = "from-A";
            await bothHaveRead.Task;
            await store.SaveAsync(a);
            firstSaveDone.SetResult();
        });

        var readB = await store.GetAsync(id);
        readB!.Purpose = "from-B";
        bothHaveRead.SetResult();
        await writerA;
        await firstSaveDone.Task;

        // B's aggregate is pinned to the pre-A revision. Its save is refused, loudly.
        var conflict = await Should.ThrowAsync<ConversationConcurrencyException>(() => store.SaveAsync(readB));
        conflict.ExpectedVersion.ShouldBeLessThan(conflict.ActualVersion);

        var reloaded = await fixture.CreateStore().GetAsync(id);
        reloaded!.Title.ShouldBe("from-A");
        reloaded.Purpose.ShouldBeNull();
    }

    [Fact]
    public async Task ReReadAndRetry_AfterAConflict_Succeeds_AndPreservesTheConcurrentPin()
    {
        // The prescribed recovery: re-read, re-apply intent, retry. Both the retried edit AND
        // the concurrently-committed pin must be present afterwards - a merge, not a clobber.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = NewConversation("agent-a", "original");
        await store.CreateAsync(conversation);
        var id = conversation.ConversationId;

        var stale = await store.GetAsync(id);
        await store.PinAsync(id, pin: true);

        stale!.Title = "renamed";
        await Should.ThrowAsync<ConversationConcurrencyException>(() => store.SaveAsync(stale));

        var fresh = await store.GetAsync(id);
        fresh!.Title = "renamed";
        await store.SaveAsync(fresh);

        var reloaded = await fixture.CreateStore().GetAsync(id);
        reloaded!.Title.ShouldBe("renamed");
        reloaded.IsPinned.ShouldBeTrue();
    }

    [Fact]
    public async Task SequentialSaves_OfTheSameAggregate_KeepWorking()
    {
        // The CAS must not break the common single-writer loop: the store re-stamps the caller's
        // aggregate with the new revision, so saving the same instance repeatedly still succeeds.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = NewConversation("agent-a", "v0");
        await store.CreateAsync(conversation);

        for (var i = 1; i <= 5; i++)
        {
            conversation.Title = $"v{i}";
            await store.SaveAsync(conversation);
        }

        var reloaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);
        reloaded!.Title.ShouldBe("v5");
    }

    [Fact]
    public async Task SaveOfAnUnversionedAggregate_IsUnconditional_SoConstructThenSaveStillWorks()
    {
        // Version 0 means "never loaded from a store". Bare construct-then-save call sites (the
        // upsert-as-create pattern) must keep working rather than being rejected as stale.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = NewConversation("agent-a", "created-via-save");
        conversation.Version.ShouldBe(0);

        await store.SaveAsync(conversation);

        var reloaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);
        reloaded!.Title.ShouldBe("created-via-save");
        reloaded.Version.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Version_IsHydratedAndAdvancesOnEveryMutation()
    {
        // The CAS token has to actually move, or every test above would pass vacuously.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = NewConversation("agent-a", "conv");
        await store.CreateAsync(conversation);
        var id = conversation.ConversationId;

        var v1 = (await fixture.CreateStore().GetAsync(id))!.Version;
        v1.ShouldBe(1);

        await store.PinAsync(id, pin: true);
        var v2 = (await fixture.CreateStore().GetAsync(id))!.Version;
        v2.ShouldBeGreaterThan(v1);

        await store.PatchMetadataAsync(id, new ConversationMetadataPatch { Purpose = FieldUpdate<string?>.Set("p") });
        var v3 = (await fixture.CreateStore().GetAsync(id))!.Version;
        v3.ShouldBeGreaterThan(v2);

        var fresh = await fixture.CreateStore().GetAsync(id);
        fresh!.Title = "t";
        await store.SaveAsync(fresh);
        var v4 = (await fixture.CreateStore().GetAsync(id))!.Version;
        v4.ShouldBeGreaterThan(v3);
    }

    [Fact]
    public async Task TouchAsync_DoesNotBumpTheVersion_SoTheHotPathCannotManufactureConflicts()
    {
        // TouchAsync only stamps UpdatedAt and runs per message. Versioning it would reject
        // legitimate saves without protecting any field.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = NewConversation("agent-a", "conv");
        await store.CreateAsync(conversation);
        var id = conversation.ConversationId;

        var snapshot = await store.GetAsync(id);
        await store.TouchAsync(id);

        snapshot!.Title = "renamed after touch";
        await store.SaveAsync(snapshot);

        var reloaded = await fixture.CreateStore().GetAsync(id);
        reloaded!.Title.ShouldBe("renamed after touch");
    }

    private static Conversation NewConversation(string agentId, string title)
        => new()
        {
            ConversationId = ConversationId.Create(),
            AgentId = AgentId.From(agentId),
            Title = title,
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static ChannelBinding NewBinding(string channelType, string channelAddress)
        => new()
        {
            BindingId = BindingId.Create(),
            ChannelType = ChannelKey.From(channelType),
            ChannelAddress = ChannelAddress.From(channelAddress),
            BoundAt = DateTimeOffset.UtcNow,
            Mode = BindingMode.Interactive,
            ThreadingMode = ThreadingMode.Single
        };
}
