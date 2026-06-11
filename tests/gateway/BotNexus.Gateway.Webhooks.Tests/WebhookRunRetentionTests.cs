using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Webhooks.Tests;

public sealed class WebhookRunRetentionTests
{
    private readonly string _dbPath;
    private readonly SqliteWebhookRunStore _store;

    public WebhookRunRetentionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"botnexus-test-retention-{Guid.NewGuid():N}.db");
        _store = new SqliteWebhookRunStore(_dbPath, logger: NullLogger<SqliteWebhookRunStore>.Instance);
    }

    private WebhookRun MakeRun(
        WebhookRunStatus status = WebhookRunStatus.Completed,
        DateTimeOffset? completedAt = null,
        DateTimeOffset? acceptedAt = null) =>
        new()
        {
            Id = WebhookRunId.From($"run_{Guid.NewGuid():N}"),
            WebhookId = WebhookId.From("wh_test123456789ab"),
            ConversationId = ConversationId.From("conv_test"),
            Status = status,
            AcceptedAt = acceptedAt ?? DateTimeOffset.UtcNow.AddDays(-60),
            CompletedAt = completedAt,
            AgentAction = true
        };

    [Fact]
    public async Task PurgeOlderThan_DeletesCompletedRunsOlderThanCutoff()
    {
        await _store.InitializeAsync();

        var old = MakeRun(WebhookRunStatus.Completed, completedAt: DateTimeOffset.UtcNow.AddDays(-45));
        var recent = MakeRun(WebhookRunStatus.Completed, completedAt: DateTimeOffset.UtcNow.AddDays(-5));
        await _store.CreateAsync(old);
        await _store.CreateAsync(recent);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var purged = await _store.PurgeOlderThanAsync(cutoff);

        Assert.Equal(1, purged);
        Assert.Null(await _store.GetAsync(old.Id));
        Assert.NotNull(await _store.GetAsync(recent.Id));
    }

    [Fact]
    public async Task PurgeOlderThan_DeletesFailedAndTimedOutRuns()
    {
        await _store.InitializeAsync();

        var failed = MakeRun(WebhookRunStatus.Failed, completedAt: DateTimeOffset.UtcNow.AddDays(-40));
        var timeout = MakeRun(WebhookRunStatus.Timeout, completedAt: DateTimeOffset.UtcNow.AddDays(-40));
        await _store.CreateAsync(failed);
        await _store.CreateAsync(timeout);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var purged = await _store.PurgeOlderThanAsync(cutoff);

        Assert.Equal(2, purged);
    }

    [Fact]
    public async Task PurgeOlderThan_PreservesRunningAndPendingRuns()
    {
        await _store.InitializeAsync();

        var pending = MakeRun(WebhookRunStatus.Pending, completedAt: null);
        var running = MakeRun(WebhookRunStatus.Running, completedAt: null);
        await _store.CreateAsync(pending);
        await _store.CreateAsync(running);

        var cutoff = DateTimeOffset.UtcNow.AddDays(1); // cutoff in the future
        var purged = await _store.PurgeOlderThanAsync(cutoff);

        Assert.Equal(0, purged);
        Assert.NotNull(await _store.GetAsync(pending.Id));
        Assert.NotNull(await _store.GetAsync(running.Id));
    }

    [Fact]
    public async Task PurgeOlderThan_ReturnsZeroWhenNothingToPurge()
    {
        await _store.InitializeAsync();

        var recent = MakeRun(WebhookRunStatus.Completed, completedAt: DateTimeOffset.UtcNow);
        await _store.CreateAsync(recent);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var purged = await _store.PurgeOlderThanAsync(cutoff);

        Assert.Equal(0, purged);
    }

    [Fact]
    public async Task PurgeOlderThan_HandlesEmptyStore()
    {
        await _store.InitializeAsync();

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var purged = await _store.PurgeOlderThanAsync(cutoff);

        Assert.Equal(0, purged);
    }
}
