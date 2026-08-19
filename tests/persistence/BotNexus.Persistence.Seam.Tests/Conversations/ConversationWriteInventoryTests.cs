using System.Reflection;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Persistence.Seam.Tests.Harness;

namespace BotNexus.Persistence.Seam.Tests.Conversations;

/// <summary>
/// The write-classification inventory for the conversations aggregate (issue #2130, acceptance
/// clauses 1 and 2), and the test that keeps it honest.
/// </summary>
/// <remarks>
/// The inventory is executable rather than prose: <see cref="EveryMutatingEntryPoint_IsClassified"/>
/// reflects over <see cref="IConversationStore"/> and fails when a new mutation entry point is added
/// without deciding how it writes and what guards it. A markdown table would have drifted silently.
/// </remarks>
public sealed class ConversationWriteInventoryTests
{
    /// <summary>
    /// Every mutating entry point on <see cref="IConversationStore"/>, how it writes, the state it
    /// owns, and what stops it losing a concurrent update.
    /// </summary>
    public static readonly IReadOnlyList<AggregateWriteEntry> Inventory =
    [
        new("conversations", nameof(IConversationStore.CreateAsync), WriteClassification.Create,
            "the whole new row",
            "Existence check under the per-conversation lock; throws when the id is taken."),

        new("conversations", nameof(IConversationStore.SaveAsync), WriteClassification.FullReplace,
            "title, purpose, status, active session, metadata, instructions, canvas html, todo, "
            + "pending prompt, overrides, pin columns, and the ENTIRE binding set (deleted and recreated)",
            "Compare-and-swap on Conversation.Version. Also CompareAndSwap: rejected with "
            + "ConversationConcurrencyException when the committed revision moved after the read. "
            + "Version 0 (never loaded from a store) writes unconditionally so construct-then-save "
            + "call sites keep working."),

        new("conversations", nameof(IConversationStore.PinAsync), WriteClassification.NarrowPatch,
            "is_pinned, pinned_at",
            "Writes only its two columns and bumps version, so a stale SaveAsync cannot unpin."),

        new("conversations", nameof(IConversationStore.ArchiveAsync), WriteClassification.NarrowPatch,
            "status, active_session_id",
            "Narrow UPDATE that bumps version; refreshes the cached revision so the next reader "
            + "can still save."),

        new("conversations", nameof(IConversationStore.TouchAsync), WriteClassification.NarrowPatch,
            "updated_at only",
            "Deliberately does NOT bump version: it runs per message and owns no field a caller "
            + "could conflict on, so versioning it would manufacture conflicts without protecting "
            + "anything."),

        new("conversations", nameof(IConversationStore.PatchMetadataAsync), WriteClassification.NarrowPatch,
            "title, purpose, instructions (only fields marked set)",
            "Builds its SET list from the patch, bumps version; unset fields are never written."),

        new("conversations", nameof(IConversationStore.PatchOverrideAsync), WriteClassification.NarrowPatch,
            "model, thinking and context-window overrides (only fields marked set)",
            "As PatchMetadataAsync; cannot revert metadata, pin or bindings."),

        new("conversations", nameof(IConversationStore.AddParticipantsAsync), WriteClassification.Merge,
            "the conversation_participants rows",
            "INSERT OR IGNORE inside a transaction — additive, idempotent, first-add-wins on the "
            + "role label. Participants are NOT written by SaveAsync at all, so concurrent "
            + "producers cannot clobber each other."),

        new("conversations", nameof(IConversationStore.AddBindingAsync), WriteClassification.NarrowPatch,
            "one conversation_bindings row",
            "Single INSERT of the new binding; touches no other binding or column."),

        new("conversations", nameof(IConversationStore.RemoveBindingAsync), WriteClassification.NarrowPatch,
            "one conversation_bindings row",
            "Single DELETE by binding id; an interleaved add of a different binding survives."),

        new("conversations", nameof(IConversationStore.MoveBindingAsync), WriteClassification.NarrowPatch,
            "the conversation_id of one binding row",
            "Single UPDATE inside a transaction; neither aggregate's other fields are rewritten."),

        new("conversations", nameof(IConversationStore.SetCanvasStateKeyAsync), WriteClassification.NarrowPatch,
            "one canvas_state row (conversation_id, key)",
            "Per-key upsert in a side table. Canvas STATE is independent of the conversation row, "
            + "so SaveAsync cannot touch it — note this is distinct from canvas_html, which "
            + "SaveAsync does replace."),

        new("conversations", nameof(IConversationStore.DeleteCanvasStateKeyAsync), WriteClassification.NarrowPatch,
            "one canvas_state row",
            "Per-key DELETE; other keys are untouched."),

        new("conversations", nameof(IConversationStore.ClearCanvasStateAsync), WriteClassification.NarrowPatch,
            "all canvas_state rows for the conversation",
            "Scoped DELETE; does not touch the conversation row."),
    ];

    [Fact]
    public void EveryMutatingEntryPoint_IsClassified()
    {
        // Guards clause 1 of #2130: the inventory must cover every mutation path, and must keep
        // covering them as the interface grows. Read-only members are excluded by name convention
        // and asserted non-empty below so the filter itself cannot silently swallow everything.
        var readOnlyPrefixes = new[] { "Get", "List", "Resolve" };

        var mutating = typeof(IConversationStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .Where(n => !readOnlyPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        mutating.ShouldNotBeEmpty();

        var classified = Inventory.Select(e => e.EntryPoint).ToHashSet(StringComparer.Ordinal);
        var unclassified = mutating.Where(n => !classified.Contains(n)).ToArray();

        unclassified.ShouldBeEmpty(
            "IConversationStore gained mutation entry points with no #2130 write classification: "
            + string.Join(", ", unclassified)
            + ". Add a row to ConversationWriteInventoryTests.Inventory stating what the write owns "
            + "and what stops it losing a concurrent update, and add a seam test if it can interleave "
            + "with SaveAsync.");
    }

    [Fact]
    public void Inventory_DoesNotNameMethodsThatNoLongerExist()
    {
        // The inverse guard: a renamed or removed entry point must not leave a stale row behind
        // claiming coverage that no longer applies.
        var actual = typeof(IConversationStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in Inventory)
            actual.ShouldContain(entry.EntryPoint);
    }

    [Fact]
    public void ExactlyOneEntryPoint_IsAFullReplace()
    {
        // The whole point of the classification: full-aggregate writes are the lost-update hazard.
        // If a second one appears it needs its own seam tests, and this test is where that gets
        // noticed rather than in production.
        var fullReplaces = Inventory
            .Where(e => e.Classification == WriteClassification.FullReplace)
            .Select(e => e.EntryPoint)
            .ToArray();

        fullReplaces.ShouldBe([nameof(IConversationStore.SaveAsync)]);
    }
}
