using BotNexus.Persistence.Sqlite.Telemetry;
using Shouldly;

namespace BotNexus.Persistence.Sqlite.Tests;

/// <summary>
/// Tests for the generic <see cref="SqliteUsageTelemetryStore"/> (#1850): namespaced counter
/// upserts, provenance/pin metadata, ordering, read paths, and namespace isolation. Uses a real
/// on-disk SQLite file in a temp dir because the store opens genuine SQLite connections
/// (MockFileSystem cannot back Microsoft.Data.Sqlite).
/// </summary>
public sealed class SqliteUsageTelemetryStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public SqliteUsageTelemetryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "botnexus-usage-telemetry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "usage.db");
    }

    private SqliteUsageTelemetryStore NewStore() => new(_dbPath);

    [Fact]
    public async Task Increment_CreatesRow_AndAccumulatesCounter()
    {
        await using var store = NewStore();

        await store.IncrementAsync("skills", "email-triage", "view");
        await store.IncrementAsync("skills", "email-triage", "view");

        var record = await store.GetAsync("skills", "email-triage");
        record.ShouldNotBeNull();
        record!.GetCounter("view").ShouldBe(2);
        record.GetCounter("use").ShouldBe(0);
        record.LastUsedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Increment_TracksSeparateCounters()
    {
        await using var store = NewStore();

        await store.IncrementAsync("skills", "calendar", "use");
        await store.IncrementAsync("skills", "calendar", "use");
        await store.IncrementAsync("skills", "calendar", "use");
        await store.IncrementAsync("skills", "calendar", "patch");

        var record = await store.GetAsync("skills", "calendar");
        record.ShouldNotBeNull();
        record!.GetCounter("use").ShouldBe(3);
        record.GetCounter("patch").ShouldBe(1);
        record.GetCounter("view").ShouldBe(0);
    }

    [Fact]
    public async Task RecordCreated_StampsCreatedBy()
    {
        await using var store = NewStore();

        await store.RecordCreatedAsync("skills", "my-skill", "agent-farnsworth");

        var record = await store.GetAsync("skills", "my-skill");
        record.ShouldNotBeNull();
        record!.CreatedBy.ShouldBe("agent-farnsworth");
        record.Pinned.ShouldBeFalse();
    }

    [Fact]
    public async Task SetPinned_TogglesPinnedFlag()
    {
        await using var store = NewStore();
        await store.IncrementAsync("skills", "pinned-skill", "use");

        await store.SetPinnedAsync("skills", "pinned-skill", true);
        (await store.GetAsync("skills", "pinned-skill"))!.Pinned.ShouldBeTrue();

        await store.SetPinnedAsync("skills", "pinned-skill", false);
        (await store.GetAsync("skills", "pinned-skill"))!.Pinned.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAll_OrdersByMostRecentlyUsedFirst()
    {
        await using var store = NewStore();

        await store.IncrementAsync("skills", "first", "use");
        await Task.Delay(10);
        await store.IncrementAsync("skills", "second", "use");

        var all = await store.GetAllAsync("skills");
        all.Count.ShouldBe(2);
        all[0].Key.ShouldBe("second");
        all[1].Key.ShouldBe("first");
    }

    [Fact]
    public async Task Counters_PersistAcrossStoreInstances()
    {
        await using (var store = NewStore())
        {
            await store.IncrementAsync("skills", "durable", "use");
            await store.IncrementAsync("skills", "durable", "use");
        }

        await using var reopened = NewStore();
        var record = await reopened.GetAsync("skills", "durable");
        record.ShouldNotBeNull();
        record!.GetCounter("use").ShouldBe(2);
    }

    // ── namespace isolation ──────────────────────────────────────────────────────

    [Fact]
    public async Task Namespaces_AreIsolated()
    {
        await using var store = NewStore();

        await store.IncrementAsync("skills", "shared-key", "use");
        await store.IncrementAsync("skills", "shared-key", "use");
        await store.IncrementAsync("plugins", "shared-key", "use");

        (await store.GetAsync("skills", "shared-key"))!.GetCounter("use").ShouldBe(2);
        (await store.GetAsync("plugins", "shared-key"))!.GetCounter("use").ShouldBe(1);

        var skillsAll = await store.GetAllAsync("skills");
        skillsAll.Count.ShouldBe(1);
        var pluginsAll = await store.GetAllAsync("plugins");
        pluginsAll.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetAll_ForUnknownNamespace_ReturnsEmpty()
    {
        await using var store = NewStore();
        await store.IncrementAsync("skills", "x", "use");
        (await store.GetAllAsync("does-not-exist")).ShouldBeEmpty();
    }

    // ── sad paths ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ForUnknownKey_ReturnsNull()
    {
        await using var store = NewStore();
        (await store.GetAsync("skills", "never-touched")).ShouldBeNull();
    }

    [Fact]
    public async Task GetAll_OnEmptyStore_ReturnsEmpty()
    {
        await using var store = NewStore();
        (await store.GetAllAsync("skills")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Increment_WithBlankKey_Throws()
    {
        await using var store = NewStore();
        await Should.ThrowAsync<ArgumentException>(async () => await store.IncrementAsync("skills", "  ", "view"));
    }

    [Fact]
    public async Task Increment_WithBlankNamespace_Throws()
    {
        await using var store = NewStore();
        await Should.ThrowAsync<ArgumentException>(async () => await store.IncrementAsync("  ", "k", "view"));
    }

    [Fact]
    public async Task Increment_WithBlankCounter_Throws()
    {
        await using var store = NewStore();
        await Should.ThrowAsync<ArgumentException>(async () => await store.IncrementAsync("skills", "k", "  "));
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
