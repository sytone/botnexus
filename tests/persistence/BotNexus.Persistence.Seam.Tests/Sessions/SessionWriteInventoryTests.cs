using System.Reflection;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Persistence.Seam.Tests.Harness;

namespace BotNexus.Persistence.Seam.Tests.Sessions;

/// <summary>
/// The write-classification inventory for the sessions aggregate (issue #3327, acceptance clause 1),
/// and the tests that keep it honest.
/// </summary>
/// <remarks>
/// Mirrors <c>ConversationWriteInventoryTests</c> from PR #3325: the inventory is executable rather
/// than prose, because a markdown table drifts silently. <see cref="EveryMutatingEntryPoint_IsClassified"/>
/// reflects over <see cref="ISessionStore"/> and fails when a mutation entry point is added without
/// deciding how it writes and what stops it losing a concurrent update.
/// </remarks>
public sealed class SessionWriteInventoryTests
{
    /// <summary>
    /// Every mutating entry point on <see cref="ISessionStore"/>, how it writes, the state it owns,
    /// and what stops it losing a concurrent update.
    /// </summary>
    public static readonly IReadOnlyList<AggregateWriteEntry> Inventory =
    [
        new("sessions", nameof(ISessionStore.GetOrCreateAsync), WriteClassification.Create,
            "the whole new in-memory session when no row exists",
            "Load-or-construct under the striped per-session lock. Constructs only; nothing is "
            + "written to SQLite until a SaveAsync, so it cannot revert a committed field."),

        new("sessions", nameof(ISessionStore.SaveAsync), WriteClassification.FullReplace,
            "every caller-owned column of the sessions row (status, metadata, conversation binding, "
            + "updated_at), plus only the unpersisted history tail during ordinary saves; explicit "
            + "destructive history mutations reconcile known rows by identity",
            "The session row remains an unconditional INSERT … ON CONFLICT DO UPDATE because pre-run "
            + "write-ahead saves must create it. History is independently guarded: ordinary saves "
            + "append only the captured delta, acknowledgements leave concurrent additions pending, "
            + "and destructive reconciliation may delete only row ids this aggregate previously "
            + "observed. Post-run finalizers still use the fenced overload to protect lifecycle fields."),

        new("sessions", nameof(ISessionStore.AppendEntriesAsync), WriteClassification.NarrowPatch,
            "new session_history rows plus updated_at",
            "Insert-only: existing history rows, metadata and status are never rewritten, so a "
            + "concurrent metadata patch or status transition survives. Refused with Conflict "
            + "against a Sealed/Expired row so an append cannot revive a terminal session; the row "
            + "is never created (NotFound). Status probe and inserts share one striped lock."),

        new("sessions", nameof(ISessionStore.PatchMetadataAsync), WriteClassification.Merge,
            "the metadata column only (keys mapped to null are removed) plus updated_at",
            "Read-merge-write of the metadata column under one striped session lock, so two "
            + "concurrent patches compose rather than clobber. Transcript and status are not in the "
            + "SET list at all, so a concurrent append or transition cannot be reverted."),

        new("sessions", nameof(ISessionStore.TransitionStatusAsync), WriteClassification.CompareAndSwap,
            "the status column only, plus updated_at",
            "Conditional UPDATE … WHERE status = $expectedStatus, so the check-then-write window is "
            + "closed at the database level as well as by the striped lock. Zero affected rows is "
            + "re-read and reported as Conflict with the authoritative status rather than claimed "
            + "as a write."),

        new("sessions", nameof(ISessionStore.DeleteAsync), WriteClassification.NarrowPatch,
            "removal of the sessions row and its session_history rows",
            "Deletes under the striped lock and evicts the cache. A finalizer save racing the "
            + "delete would resurrect the row via the unconditional upsert — which is precisely "
            + "what the #1518 fence overload exists to suppress."),

        new("sessions", nameof(ISessionStore.ArchiveAsync), WriteClassification.NarrowPatch,
            "status (sealed) and updated_at; history rows are left untouched",
            "Drains the active run for the exact session BEFORE taking the lock (#2903), so its "
            + "final append reaches durable storage before the status transition. A drain timeout "
            + "throws and nothing is written at all. The re-load happens inside the lock, so the "
            + "sealed aggregate is authoritative rather than a caller's stale snapshot."),

        new("sessions", nameof(ISessionStore.SaveSubAgentSessionAsync), WriteClassification.Create,
            "one sub_agent_sessions row at spawn time",
            "Insert of a row keyed by sub-agent id in a side table the session aggregate never "
            + "rewrites; no session column is touched."),

        new("sessions", nameof(ISessionStore.UpdateSubAgentSessionAsync), WriteClassification.NarrowPatch,
            "ended_at and status of one sub_agent_sessions row",
            "Narrow UPDATE of the completion columns only; cannot revert the spawn-time fields or "
            + "any part of the parent session."),
    ];

    /// <summary>
    /// The fenced <see cref="ISessionStore.SaveAsync(GatewaySession, SessionWriteFence, CancellationToken)"/>
    /// overload. It shares a name with the unfenced aggregate save, so it cannot be a distinct
    /// <see cref="Inventory"/> row keyed by method name — but it is a genuinely different write
    /// shape and is classified here so the distinction is executable rather than implied.
    /// </summary>
    public static readonly AggregateWriteEntry FencedSave =
        new("sessions", nameof(ISessionStore.SaveAsync), WriteClassification.Fenced,
            "the same session columns and append/targeted history delta as the unfenced save",
            "SessionFenceEvaluator.Passes over a lock-scoped re-read of (status, conversation_id) "
            + "straight from SQLite: the write is skipped as Rebound when the row was deleted, "
            + "sealed/expired by a competing reset, or rebound to another conversation while the "
            + "run was in flight (#1518). The re-read and the upsert share ONE striped lock, so the "
            + "check-then-write window is closed.");

    [Fact]
    public void EveryMutatingEntryPoint_IsClassified()
    {
        // Guards clause 1 of #3327: the inventory must cover every mutation path and keep covering
        // them as the interface grows. Read-only members are excluded by name convention and the
        // remaining set is asserted non-empty, so a filter that swallowed everything fails loudly
        // instead of classifying zero methods and reporting green.
        var readOnlyPrefixes = new[] { "Get", "List", "Resolve" };

        var mutating = typeof(ISessionStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .Where(n => !readOnlyPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        mutating.ShouldNotBeEmpty();

        var classified = Inventory.Select(e => e.EntryPoint).ToHashSet(StringComparer.Ordinal);
        var unclassified = mutating.Where(n => !classified.Contains(n)).ToArray();

        unclassified.ShouldBeEmpty(
            "ISessionStore gained mutation entry points with no #2130/#3327 write classification: "
            + string.Join(", ", unclassified)
            + ". Add a row to SessionWriteInventoryTests.Inventory stating what the write owns and "
            + "what stops it losing a concurrent update, and add a seam test if it can interleave "
            + "with SaveAsync.");
    }

    [Fact]
    public void GetOrCreate_IsClassifiedDespiteItsReadShapedName()
    {
        // GetOrCreateAsync starts with "Get" and would therefore be filtered out of the reflected
        // mutating set. It is a creation entry point, so it is pinned explicitly rather than left
        // to a naming convention that happens to exclude it.
        Inventory.ShouldContain(e => e.EntryPoint == nameof(ISessionStore.GetOrCreateAsync));
    }

    [Fact]
    public void Inventory_DoesNotNameMethodsThatNoLongerExist()
    {
        // The inverse guard: a renamed or removed entry point must not leave a stale row behind
        // claiming coverage that no longer applies.
        var actual = typeof(ISessionStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        actual.ShouldNotBeEmpty();

        foreach (var entry in Inventory)
            actual.ShouldContain(entry.EntryPoint);

        actual.ShouldContain(FencedSave.EntryPoint);
    }

    [Fact]
    public void TheFullReplaceWrite_IsOnlyTheUnfencedSessionRowUpsert()
    {
        // History is no longer part of a full replacement: ordinary saves append their delta and
        // explicit destructive mutations reconcile known row identities. The remaining full-replace
        // classification is the caller-owned sessions row, whose unconditional upsert is required
        // for pre-run creation and remains the reason post-run callers use the fenced overload.
        var fullReplaces = Inventory
            .Where(e => e.Classification == WriteClassification.FullReplace)
            .Select(e => e.EntryPoint)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        fullReplaces.ShouldBe([nameof(ISessionStore.SaveAsync)]);
    }

    [Fact]
    public void SaveAsync_HasBothAnUnfencedAndAFencedOverload_AndTheyAreClassifiedDifferently()
    {
        // #1518's fence is only meaningful if the unfenced overload still exists for the pre-run
        // write-ahead saves. If either overload disappeared, the "use the fenced one in finalizers"
        // guidance in the docs would be describing an API that no longer has the shape it claims.
        var overloads = typeof(ISessionStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == nameof(ISessionStore.SaveAsync))
            .ToArray();

        overloads.Length.ShouldBe(2);
        overloads.ShouldContain(m => m.GetParameters().Any(p => p.ParameterType == typeof(SessionWriteFence)));

        var unfenced = Inventory.Single(e => e.EntryPoint == nameof(ISessionStore.SaveAsync));
        unfenced.Classification.ShouldBe(WriteClassification.FullReplace);
        FencedSave.Classification.ShouldBe(WriteClassification.Fenced);
    }
}
