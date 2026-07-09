using System.IO.Abstractions.TestingHelpers;
using BotNexus.Extensions.Skills.Telemetry;
using Shouldly;

namespace BotNexus.Skills.Tests;

/// <summary>
/// Tests for <see cref="SqliteSkillUsageStore"/> (#1833): counter increments, upsert-on-first-touch,
/// provenance/pin metadata, ordering, and read paths. Uses a real on-disk SQLite file in a temp dir
/// because the store opens genuine SQLite connections (MockFileSystem cannot back Microsoft.Data.Sqlite).
/// </summary>
public sealed class SqliteSkillUsageStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public SqliteSkillUsageStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "botnexus-skill-telemetry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "skill-usage.db");
    }

    private SqliteSkillUsageStore NewStore() => new(_dbPath);

    // ── happy paths ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordView_CreatesRow_AndIncrementsViewCount()
    {
        await using var store = NewStore();

        await store.RecordViewAsync("email-triage");
        await store.RecordViewAsync("email-triage");

        var record = await store.GetAsync("email-triage");
        record.ShouldNotBeNull();
        record!.ViewCount.ShouldBe(2);
        record.UseCount.ShouldBe(0);
        record.PatchCount.ShouldBe(0);
        record.LastUsedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task RecordUse_And_RecordPatch_TrackSeparateCounters()
    {
        await using var store = NewStore();

        await store.RecordUseAsync("calendar");
        await store.RecordUseAsync("calendar");
        await store.RecordUseAsync("calendar");
        await store.RecordPatchAsync("calendar");

        var record = await store.GetAsync("calendar");
        record.ShouldNotBeNull();
        record!.UseCount.ShouldBe(3);
        record.PatchCount.ShouldBe(1);
        record.ViewCount.ShouldBe(0);
    }

    [Fact]
    public async Task RecordCreated_StampsCreatedBy()
    {
        await using var store = NewStore();

        await store.RecordCreatedAsync("my-skill", "agent-farnsworth");

        var record = await store.GetAsync("my-skill");
        record.ShouldNotBeNull();
        record!.CreatedBy.ShouldBe("agent-farnsworth");
        record.Pinned.ShouldBeFalse();
    }

    [Fact]
    public async Task SetPinned_TogglesPinnedFlag()
    {
        await using var store = NewStore();
        await store.RecordUseAsync("pinned-skill");

        await store.SetPinnedAsync("pinned-skill", true);
        (await store.GetAsync("pinned-skill"))!.Pinned.ShouldBeTrue();

        await store.SetPinnedAsync("pinned-skill", false);
        (await store.GetAsync("pinned-skill"))!.Pinned.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAll_OrdersByMostRecentlyUsedFirst()
    {
        await using var store = NewStore();

        await store.RecordUseAsync("first");
        await Task.Delay(10);
        await store.RecordUseAsync("second");

        var all = await store.GetAllAsync();
        all.Count.ShouldBe(2);
        all[0].SkillName.ShouldBe("second");
        all[1].SkillName.ShouldBe("first");
    }

    [Fact]
    public async Task Counters_PersistAcrossStoreInstances()
    {
        await using (var store = NewStore())
        {
            await store.RecordUseAsync("durable");
            await store.RecordUseAsync("durable");
        }

        await using var reopened = NewStore();
        var record = await reopened.GetAsync("durable");
        record.ShouldNotBeNull();
        record!.UseCount.ShouldBe(2);
    }

    // ── sad paths ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ForUnknownSkill_ReturnsNull()
    {
        await using var store = NewStore();
        (await store.GetAsync("never-touched")).ShouldBeNull();
    }

    [Fact]
    public async Task GetAll_OnEmptyStore_ReturnsEmpty()
    {
        await using var store = NewStore();
        (await store.GetAllAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task RecordView_WithBlankSkillName_Throws()
    {
        await using var store = NewStore();
        await Should.ThrowAsync<ArgumentException>(async () => await store.RecordViewAsync("  "));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup; a locked WAL file on Windows must not fail the test run.
        }
    }
}
