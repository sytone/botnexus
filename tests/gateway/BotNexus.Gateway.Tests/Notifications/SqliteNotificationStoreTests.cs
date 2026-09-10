using BotNexus.Gateway.Abstractions.Notifications;
using BotNexus.Gateway.Notifications;

namespace BotNexus.Gateway.Tests.Notifications;

/// <summary>
/// Pins the notification store.
/// </summary>
/// <remarks>
/// Read state is the part that has to be right. It lives server-side precisely so dismissing
/// something on a laptop does not leave it unread on a phone, so the tests read back through a
/// FRESH store rather than the instance that wrote - an in-memory cache would satisfy a
/// same-instance read and lose everything on restart, which is the one failure this store exists
/// to prevent.
/// </remarks>
public sealed class SqliteNotificationStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "botnexus-notifications", Guid.NewGuid().ToString("N"));

    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero));

    private string DbPath => Path.Combine(_dir, "notifications.sqlite");

    public SqliteNotificationStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Release THIS database's pooled handles before deleting. ClearAllPools would work too and
        // is process-global, which disposes native handles belonging to tests running in parallel -
        // the failure then names an innocent test and will not reproduce alone.
        SqlitePoolCleanup.ClearPoolFor(DbPath);

        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    private SqliteNotificationStore Store() => new(DbPath, timeProvider: _time);

    private static Notification New(
        string title = "Run finished",
        NotificationKind kind = NotificationKind.AgentRunCompleted,
        NotificationSeverity severity = NotificationSeverity.Info) => new()
    {
        Id = string.Empty,
        Kind = kind,
        Severity = severity,
        Title = title,
        CreatedAtUtc = default,
    };

    [Fact]
    public async Task Appends_and_reads_back_through_a_fresh_store()
    {
        var appended = await Store().AppendAsync(New(title: "Agent finished") with
        {
            Body = "Took 12 seconds",
            AgentId = "assistant",
            ConversationId = "c_1",
            Link = "/agent/assistant",
        });

        var all = await Store().ListAsync();

        var read = Assert.Single(all);
        Assert.Equal(appended.Id, read.Id);
        Assert.Equal("Agent finished", read.Title);
        Assert.Equal("Took 12 seconds", read.Body);
        Assert.Equal("assistant", read.AgentId);
        Assert.Equal("c_1", read.ConversationId);
        Assert.Equal("/agent/assistant", read.Link);
        Assert.Null(read.ReadAtUtc);
    }

    // Identity and time belong to the store, so two sources cannot collide on an id or disagree
    // about ordering.
    [Fact]
    public async Task Assigns_an_id_and_timestamp_when_the_caller_supplies_none()
    {
        var stored = await Store().AppendAsync(New());

        Assert.NotEmpty(stored.Id);
        Assert.Equal(_time.GetUtcNow(), stored.CreatedAtUtc);
    }

    [Fact]
    public async Task Lists_newest_first()
    {
        var store = Store();
        await store.AppendAsync(New(title: "first"));
        _time.Advance(TimeSpan.FromMinutes(1));
        await store.AppendAsync(New(title: "second"));
        _time.Advance(TimeSpan.FromMinutes(1));
        await store.AppendAsync(New(title: "third"));

        var titles = (await Store().ListAsync()).Select(n => n.Title);

        Assert.Equal(["third", "second", "first"], titles);
    }

    [Fact]
    public async Task Unread_count_and_filter_track_read_state()
    {
        var store = Store();
        var one = await store.AppendAsync(New(title: "one"));
        await store.AppendAsync(New(title: "two"));

        Assert.Equal(2, await store.UnreadCountAsync());

        Assert.True(await store.MarkReadAsync(one.Id));

        // Fresh store: read state must be on disk, not in the instance that wrote it.
        var fresh = Store();
        Assert.Equal(1, await fresh.UnreadCountAsync());
        Assert.Equal(["two"], (await fresh.ListAsync(includeRead: false)).Select(n => n.Title));
        Assert.Equal(2, (await fresh.ListAsync(includeRead: true)).Count);
    }

    // A second read must not move a timestamp that already recorded when it was actually seen.
    [Fact]
    public async Task Marking_an_already_read_notification_changes_nothing()
    {
        var store = Store();
        var stored = await store.AppendAsync(New());
        Assert.True(await store.MarkReadAsync(stored.Id));
        var firstReadAt = (await store.ListAsync()).Single().ReadAtUtc;

        _time.Advance(TimeSpan.FromHours(1));
        Assert.False(await store.MarkReadAsync(stored.Id));

        Assert.Equal(firstReadAt, (await Store().ListAsync()).Single().ReadAtUtc);
    }

    [Fact]
    public async Task Mark_all_read_reports_how_many_changed()
    {
        var store = Store();
        await store.AppendAsync(New(title: "a"));
        await store.AppendAsync(New(title: "b"));
        await store.MarkReadAsync((await store.ListAsync()).First().Id);

        Assert.Equal(1, await store.MarkAllReadAsync());
        Assert.Equal(0, await Store().UnreadCountAsync());
        Assert.Equal(0, await Store().MarkAllReadAsync());
    }

    [Fact]
    public async Task Unknown_ids_report_false_rather_than_throwing()
    {
        var store = Store();

        Assert.False(await store.MarkReadAsync("no-such-id"));
        Assert.False(await store.DeleteAsync("no-such-id"));
        Assert.False(await store.MarkReadAsync(""));
        Assert.False(await store.DeleteAsync("   "));
    }

    [Fact]
    public async Task Delete_removes_one_notification()
    {
        var store = Store();
        var one = await store.AppendAsync(New(title: "one"));
        await store.AppendAsync(New(title: "two"));

        Assert.True(await store.DeleteAsync(one.Id));

        Assert.Equal(["two"], (await Store().ListAsync()).Select(n => n.Title));
    }

    // A gateway running cron jobs overnight would otherwise accumulate an unbounded log.
    [Fact]
    public async Task Prune_removes_old_read_notifications()
    {
        var store = Store();
        var old = await store.AppendAsync(New(title: "old"));
        await store.MarkReadAsync(old.Id);

        _time.Advance(TimeSpan.FromDays(40));
        await store.AppendAsync(New(title: "recent"));

        Assert.Equal(1, await store.PruneReadAsync(TimeSpan.FromDays(30)));
        Assert.Equal(["recent"], (await Store().ListAsync()).Select(n => n.Title));
    }

    // The clause that matters: pruning something nobody has seen would discard the very thing the
    // feature exists to surface.
    [Fact]
    public async Task Prune_never_removes_an_unread_notification()
    {
        var store = Store();
        await store.AppendAsync(New(title: "unread and ancient"));

        _time.Advance(TimeSpan.FromDays(400));

        Assert.Equal(0, await store.PruneReadAsync(TimeSpan.FromDays(30)));
        Assert.Single(await Store().ListAsync());
    }

    [Fact]
    public async Task Limit_caps_the_result_set_keeping_the_newest()
    {
        var store = Store();
        for (var i = 0; i < 5; i++)
        {
            await store.AppendAsync(New(title: $"n{i}"));
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        var titles = (await store.ListAsync(limit: 2)).Select(n => n.Title);

        Assert.Equal(["n4", "n3"], titles);
    }

    [Fact]
    public async Task Kind_and_severity_survive_the_round_trip()
    {
        await Store().AppendAsync(New(
            kind: NotificationKind.AgentWaitingForInput,
            severity: NotificationSeverity.Warning));

        var stored = Assert.Single(await Store().ListAsync());
        Assert.Equal(NotificationKind.AgentWaitingForInput, stored.Kind);
        Assert.Equal(NotificationSeverity.Warning, stored.Severity);
    }
}
