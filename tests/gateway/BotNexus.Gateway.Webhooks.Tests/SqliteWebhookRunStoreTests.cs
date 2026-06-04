using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Webhooks;

namespace BotNexus.Gateway.Webhooks.Tests;

/// <summary>
/// Integration-style tests for <see cref="SqliteWebhookRunStore"/> using
/// a real SQLite file on a temp path.
/// </summary>
public sealed class SqliteWebhookRunStoreTests : IAsyncLifetime
{
    private SqliteWebhookRunStore _store = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"wh-run-tests-{Guid.NewGuid():N}.db");
        _store = new SqliteWebhookRunStore(_dbPath);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    private static WebhookRun MakeRun(string? webhookId = null) =>
        new()
        {
            Id = WebhookRunId.Create(),
            WebhookId = WebhookId.From(webhookId ?? "wh_testwebhook123456"),
            ConversationId = ConversationId.Create(),
            Status = WebhookRunStatus.Pending,
            AcceptedAt = DateTimeOffset.UtcNow,
            AgentAction = true
        };

    [Fact]
    public async Task CreateAndGet_RoundTrips()
    {
        var run = MakeRun();
        await _store.CreateAsync(run);

        var retrieved = await _store.GetAsync(run.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(run.Id.Value, retrieved.Id.Value);
        Assert.Equal(run.WebhookId.Value, retrieved.WebhookId.Value);
        Assert.Equal(WebhookRunStatus.Pending, retrieved.Status);
        Assert.Null(retrieved.AgentResponse);
        Assert.True(retrieved.AgentAction);
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNull()
    {
        var result = await _store.GetAsync(WebhookRunId.Create());
        Assert.Null(result);
    }

    [Fact]
    public async Task Update_ChangesStatus()
    {
        var run = await _store.CreateAsync(MakeRun());

        run.Status = WebhookRunStatus.Completed;
        run.AgentResponse = "The answer is 42.";
        run.CompletedAt = DateTimeOffset.UtcNow;
        await _store.UpdateAsync(run);

        var retrieved = await _store.GetAsync(run.Id);
        Assert.Equal(WebhookRunStatus.Completed, retrieved!.Status);
        Assert.Equal("The answer is 42.", retrieved.AgentResponse);
        Assert.NotNull(retrieved.CompletedAt);
    }

    [Fact]
    public async Task Update_FailedStatus_PersistsError()
    {
        var run = await _store.CreateAsync(MakeRun());

        run.Status = WebhookRunStatus.Failed;
        run.Error = "Agent timed out.";
        run.CompletedAt = DateTimeOffset.UtcNow;
        await _store.UpdateAsync(run);

        var retrieved = await _store.GetAsync(run.Id);
        Assert.Equal(WebhookRunStatus.Failed, retrieved!.Status);
        Assert.Equal("Agent timed out.", retrieved.Error);
    }

    [Fact]
    public async Task ListByWebhook_ReturnsMostRecentFirst()
    {
        const string webhookId = "wh_testwebhook123456";
        for (var i = 0; i < 5; i++)
        {
            var run = MakeRun(webhookId) with { AcceptedAt = DateTimeOffset.UtcNow.AddSeconds(-i) };
            await _store.CreateAsync(run);
        }

        // Different webhook — should not appear
        await _store.CreateAsync(MakeRun("wh_otherwebhook12345"));

        var runs = await _store.ListByWebhookAsync(WebhookId.From(webhookId), limit: 3);
        Assert.Equal(3, runs.Count);
        // Verify descending order
        for (var i = 0; i < runs.Count - 1; i++)
            Assert.True(runs[i].AcceptedAt >= runs[i + 1].AcceptedAt);
    }

    [Fact]
    public async Task ListByWebhook_EmptyForUnknownWebhook()
    {
        var runs = await _store.ListByWebhookAsync(WebhookId.Create());
        Assert.Empty(runs);
    }

    [Fact]
    public async Task Run_WithCallbackUrl_RoundTrips()
    {
        var run = MakeRun() with { CallbackUrl = "https://example.com/callback" };
        await _store.CreateAsync(run);

        var retrieved = await _store.GetAsync(run.Id);
        Assert.Equal("https://example.com/callback", retrieved!.CallbackUrl);
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        await _store.InitializeAsync();
        await _store.InitializeAsync();
    }
}
