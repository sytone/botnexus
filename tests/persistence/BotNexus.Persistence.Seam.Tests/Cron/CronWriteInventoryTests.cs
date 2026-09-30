using System.Reflection;
using BotNexus.Cron;
using BotNexus.Persistence.Seam.Tests.Harness;

namespace BotNexus.Persistence.Seam.Tests.Cron;

/// <summary>
/// The executable write-classification inventory for cron jobs and runs (issue #3327,
/// acceptance clause 3).
/// </summary>
/// <remarks>
/// Cron deliberately has no full-row replacement write. Definition edits, scheduler bookkeeping,
/// run history and the conversation reservation each own separate columns or rows. Reflecting over
/// <see cref="ICronStore"/> keeps that boundary visible when the interface grows.
/// </remarks>
public sealed class CronWriteInventoryTests
{
    public static readonly IReadOnlyList<AggregateWriteEntry> Inventory =
    [
        new("cron jobs", nameof(ICronStore.CreateAsync), WriteClassification.Create,
            "one new cron_jobs row",
            "Primary-key uniqueness rejects a duplicate job id; the store owns created_at and "
            + "schedule_activated_at initialization."),

        new("cron jobs", nameof(ICronStore.UpdateDefinitionAsync), WriteClassification.NarrowPatch,
            "caller-authored definition columns and schedule_activated_at when scheduling inputs change",
            "The UPDATE omits next_run_at, backoff_until, last_run_* and conversation_id. Agent-owned "
            + "updates may additionally compare-and-swap the authorization-time ownership pair."),

        new("cron jobs", nameof(ICronStore.SetNextRunAtAsync), WriteClassification.NarrowPatch,
            "next_run_at only",
            "Single-column scheduler write; definition, run bookkeeping, backoff and conversation "
            + "reservation remain untouched."),

        new("cron jobs", nameof(ICronStore.SetBackoffUntilAsync), WriteClassification.NarrowPatch,
            "backoff_until only",
            "Single-column job-authored wake floor; ordinary rescheduling cannot clear it."),

        new("cron jobs", nameof(ICronStore.RecordRunFinalizationAsync), WriteClassification.NarrowPatch,
            "last_run_at, last_run_status and last_run_error",
            "Terminal scheduler bookkeeping never rewrites definition, next-run, backoff or "
            + "conversation state."),

        new("cron jobs", nameof(ICronStore.TrySetConversationIdAsync), WriteClassification.CompareAndSwap,
            "conversation_id only",
            "Conditional UPDATE WHERE conversation_id IS NULL; the following read returns the "
            + "winner when concurrent first runs race."),

        new("cron jobs", nameof(ICronStore.DeleteAsync), WriteClassification.NarrowPatch,
            "the selected cron_jobs row and its cron_runs rows",
            "Deletes by job id under the store write lock; no other job is touched."),

        new("cron runs", nameof(ICronStore.RecordRunStartAsync), WriteClassification.Create,
            "one running cron_runs row plus the owning job's last_run_* bookkeeping",
            "The run id is unique and the job update is limited to last_run_at/status/error; no "
            + "definition, scheduling, backoff or conversation column is rewritten."),

        new("cron runs", nameof(ICronStore.TryRecordMissedRunAsync), WriteClassification.Merge,
            "one terminal missed cron_runs row for a scheduled occurrence",
            "INSERT OR IGNORE plus the unique (job_id, started_at) missed-run index makes repeated "
            + "or concurrent startup scans converge without touching job bookkeeping."),

        new("cron runs", nameof(ICronStore.RecordRunSessionAsync), WriteClassification.NarrowPatch,
            "session_id of one running cron_runs row",
            "Conditional UPDATE by run id and running status; terminal outcome and cost columns are "
            + "not rewritten."),

        new("cron runs", nameof(ICronStore.RecordRunCompleteAsync), WriteClassification.NarrowPatch,
            "terminal outcome and optional session/cost columns of one cron_runs row",
            "Narrow UPDATE by run id; COALESCE preserves previously measured optional session and "
            + "cost values when a later amendment supplies no replacement."),

        new("cron runs", nameof(ICronStore.PurgeRunsOlderThanAsync), WriteClassification.NarrowPatch,
            "eligible terminal cron_runs rows older than the retention cutoff",
            "Predicate-scoped DELETE excludes running and unknown-status rows and never touches "
            + "cron_jobs."),
    ];

    [Fact]
    public void EveryMutatingEntryPoint_IsClassified()
    {
        var readOnlyPrefixes = new[] { "Get", "List" };

        var mutating = typeof(ICronStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name)
            .Where(name => name != nameof(ICronStore.InitializeAsync))
            .Where(name => !readOnlyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        mutating.ShouldNotBeEmpty();

        var classified = Inventory.Select(entry => entry.EntryPoint).ToArray();
        classified.ShouldNotBeEmpty();
        classified.ShouldBeUnique();

        classified.Order(StringComparer.Ordinal).ShouldBe(
            mutating.Order(StringComparer.Ordinal),
            "The ICronStore mutation set and #3327 inventory must match exactly. A method present only "
            + "in the interface is unclassified; a row present only in the inventory is stale.");
    }

    [Fact]
    public void Inventory_DoesNotNameMethodsThatNoLongerExist()
    {
        var actual = typeof(ICronStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        actual.ShouldNotBeEmpty();

        foreach (var entry in Inventory)
            actual.ShouldContain(entry.EntryPoint);
    }

    [Fact]
    public void DefinitionRunBookkeepingAndConversationReservation_HaveIndependentWriteShapes()
    {
        Inventory.Single(entry => entry.EntryPoint == nameof(ICronStore.UpdateDefinitionAsync))
            .Classification.ShouldBe(WriteClassification.NarrowPatch);
        Inventory.Single(entry => entry.EntryPoint == nameof(ICronStore.RecordRunFinalizationAsync))
            .Classification.ShouldBe(WriteClassification.NarrowPatch);
        Inventory.Single(entry => entry.EntryPoint == nameof(ICronStore.TrySetConversationIdAsync))
            .Classification.ShouldBe(WriteClassification.CompareAndSwap);
    }

    [Fact]
    public void CronStore_HasNoFullReplaceWrite()
    {
        Inventory.ShouldNotContain(entry => entry.Classification == WriteClassification.FullReplace);
    }
}
