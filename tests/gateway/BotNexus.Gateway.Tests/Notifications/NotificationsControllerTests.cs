using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Abstractions.Notifications;
using BotNexus.Gateway.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests.Notifications;

/// <summary>
/// Pins the notifications REST API.
/// </summary>
/// <remarks>
/// This is the surface a future phone or desktop client is written against, so the tests care about
/// the CONTRACT as much as the behaviour: kind and severity are emitted as names rather than the
/// integers they are stored as, because a client should not have to track which number means
/// "waiting for input", and a renumbering must not silently change what an installed client shows.
/// </remarks>
public sealed class NotificationsControllerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "botnexus-notif-api", Guid.NewGuid().ToString("N"));

    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero));

    private string DbPath => Path.Combine(_dir, "notifications.sqlite");

    public NotificationsControllerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
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

    private INotificationStore Store() => new SqliteNotificationStore(DbPath, timeProvider: _time);

    private NotificationsController Controller(INotificationPublisher? publisher = null) =>
        new(Store(), publisher ?? new NotificationPublisher(Store()));

    private async Task<Notification> Seed(
        string title = "Run finished",
        NotificationKind kind = NotificationKind.AgentRunCompleted,
        NotificationSeverity severity = NotificationSeverity.Info) =>
        await Store().AppendAsync(new Notification
        {
            Id = string.Empty,
            Kind = kind,
            Severity = severity,
            Title = title,
            CreatedAtUtc = default,
        });

    private static IReadOnlyList<NotificationResponse> Body(
        ActionResult<IReadOnlyList<NotificationResponse>> result) =>
        Assert.IsAssignableFrom<IReadOnlyList<NotificationResponse>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

    // Every other notification is raised by something failing, which makes the feature awkward to
    // check: you would have to break something to learn whether being told works. The test
    // endpoint goes through the ordinary publisher, so it exercises the real path rather than
    // writing to the store behind its back - which would prove nothing about the parts that fail.
    [Fact]
    public async Task Raises_a_test_notification_through_the_publisher()
    {
        var spy = new SpyPublisher();

        var result = await Controller(spy).RaiseTest();

        Assert.IsType<AcceptedResult>(result);
        var raised = Assert.Single(spy.Published);
        Assert.Equal(NotificationSeverity.Info, raised.Severity);
        Assert.Contains("Test", raised.Title, StringComparison.OrdinalIgnoreCase);
    }

    // It has to land in the store like anything else, or "send a test" would prove the endpoint
    // works while the thing it is testing does not.
    [Fact]
    public async Task A_test_notification_is_stored_and_counts_as_unread()
    {
        await Controller().RaiseTest();

        var body = Body(await Controller().List());
        var stored = Assert.Single(body);
        Assert.Null(stored.ReadAtUtc);

        var count = Assert.IsType<OkObjectResult>((await Controller().UnreadCount()).Result).Value;
        Assert.Equal(1, Assert.IsType<UnreadCountResponse>(count).Count);
    }

    /// <summary>Records what was published without storing it.</summary>
    private sealed class SpyPublisher : INotificationPublisher
    {
        public List<Notification> Published { get; } = [];

        public Task PublishAsync(Notification notification, CancellationToken ct = default)
        {
            Published.Add(notification);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Lists_newest_first()
    {
        await Seed("first");
        _time.Advance(TimeSpan.FromMinutes(1));
        await Seed("second");

        var body = Body(await Controller().List());

        Assert.Equal(["second", "first"], body.Select(n => n.Title));
    }

    // The contract a mobile client binds to.
    [Fact]
    public async Task Emits_kind_and_severity_as_names_not_numbers()
    {
        await Seed(kind: NotificationKind.AgentWaitingForInput, severity: NotificationSeverity.Warning);

        var one = Assert.Single(Body(await Controller().List()));

        Assert.Equal("AgentWaitingForInput", one.Kind);
        Assert.Equal("Warning", one.Severity);
    }

    [Fact]
    public async Task Can_return_only_unread()
    {
        var read = await Seed("read one");
        await Seed("unread one");
        await Store().MarkReadAsync(read.Id);

        var body = Body(await Controller().List(includeRead: false));

        Assert.Equal(["unread one"], body.Select(n => n.Title));
    }

    // A client asking for the whole history must not be able to have it.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10_000)]
    public async Task Clamps_an_out_of_range_limit_instead_of_failing(int limit)
    {
        await Seed();

        var result = await Controller().List(limit: limit);

        Assert.Single(Body(result));
    }

    [Fact]
    public async Task Unread_count_is_available_without_fetching_content()
    {
        await Seed("a");
        await Seed("b");

        var result = await Controller().UnreadCount();

        Assert.Equal(2, Assert.IsType<UnreadCountResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value).Count);
    }

    [Fact]
    public async Task Marking_read_returns_no_content_and_persists()
    {
        var seeded = await Seed();

        Assert.IsType<NoContentResult>(await Controller().MarkRead(seeded.Id));

        Assert.Equal(0, await Store().UnreadCountAsync());
    }

    // Unknown and already-read are the same answer: there is no unread notification by that id.
    [Fact]
    public async Task Marking_an_unknown_or_already_read_notification_is_not_found()
    {
        var seeded = await Seed();
        await Store().MarkReadAsync(seeded.Id);

        Assert.Equal(StatusCodes.Status404NotFound,
            Assert.IsType<NotFoundObjectResult>(await Controller().MarkRead(seeded.Id)).StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound,
            Assert.IsType<NotFoundObjectResult>(await Controller().MarkRead("ghost")).StatusCode);
    }

    [Fact]
    public async Task Read_all_reports_how_many_changed()
    {
        await Seed("a");
        await Seed("b");

        var result = await Controller().MarkAllRead();

        Assert.Equal(2, Assert.IsType<MarkAllReadResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value).Changed);
        Assert.Equal(0, await Store().UnreadCountAsync());
    }

    [Fact]
    public async Task Delete_removes_the_notification()
    {
        var seeded = await Seed();

        Assert.IsType<NoContentResult>(await Controller().Delete(seeded.Id));

        Assert.Empty(await Store().ListAsync());
    }

    [Fact]
    public async Task Deleting_an_unknown_notification_is_not_found()
    {
        Assert.IsType<NotFoundObjectResult>(await Controller().Delete("ghost"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_id_is_a_bad_request_rather_than_a_not_found(string id)
    {
        Assert.IsType<BadRequestObjectResult>(await Controller().MarkRead(id));
        Assert.IsType<BadRequestObjectResult>(await Controller().Delete(id));
    }

    // An empty store is an empty list, not an error - the ordinary state of a quiet gateway.
    [Fact]
    public async Task An_empty_store_lists_nothing_and_counts_zero()
    {
        Assert.Empty(Body(await Controller().List()));
        Assert.Equal(0, Assert.IsType<UnreadCountResponse>(
            Assert.IsType<OkObjectResult>((await Controller().UnreadCount()).Result).Value).Count);
    }
}
