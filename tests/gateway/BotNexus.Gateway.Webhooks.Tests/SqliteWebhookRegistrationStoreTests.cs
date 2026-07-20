using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Webhooks;

namespace BotNexus.Gateway.Webhooks.Tests;

/// <summary>
/// Integration-style tests for <see cref="SqliteWebhookRegistrationStore"/> using
/// a real SQLite file on a temp path. Fast — no network, no gateway, no DI.
/// </summary>
public sealed class SqliteWebhookRegistrationStoreTests : IAsyncLifetime
{
    private SqliteWebhookRegistrationStore _store = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"wh-reg-tests-{Guid.NewGuid():N}.db");
        _store = new SqliteWebhookRegistrationStore(_dbPath);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    private static WebhookRegistration MakeRegistration(string? label = null, string? agentId = null) =>
        new()
        {
            Id = WebhookId.Create(),
            Label = label ?? "test-webhook",
            AgentId = AgentId.From(agentId ?? "test-agent"),
            Secret = WebhookSecretHelper.GenerateSecret(),
            DefaultResponseMode = WebhookResponseMode.Async,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task CreateAndGet_RoundTrips()
    {
        var reg = MakeRegistration();
        await _store.CreateAsync(reg);

        var retrieved = await _store.GetAsync(reg.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(reg.Id.Value, retrieved.Id.Value);
        Assert.Equal(reg.Label, retrieved.Label);
        Assert.Equal(reg.AgentId.Value, retrieved.AgentId.Value);
        Assert.Equal(reg.Secret, retrieved.Secret);
        Assert.Equal(reg.DefaultResponseMode, retrieved.DefaultResponseMode);
        Assert.True(retrieved.Enabled);
        Assert.Null(retrieved.PinnedConversationId);
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNull()
    {
        var result = await _store.GetAsync(WebhookId.Create());
        Assert.Null(result);
    }

    [Fact]
    public async Task List_ByAgentId_FiltersCorrectly()
    {
        var a1 = await _store.CreateAsync(MakeRegistration(agentId: "agent-a"));
        var a2 = await _store.CreateAsync(MakeRegistration(agentId: "agent-a"));
        var b1 = await _store.CreateAsync(MakeRegistration(agentId: "agent-b"));

        var listA = await _store.ListAsync(AgentId.From("agent-a"));
        Assert.Equal(2, listA.Count);
        Assert.All(listA, r => Assert.Equal("agent-a", r.AgentId.Value));

        var listAll = await _store.ListAsync();
        Assert.Equal(3, listAll.Count);
    }

    [Fact]
    public async Task Update_ChangesLabel()
    {
        var reg = await _store.CreateAsync(MakeRegistration(label: "original"));
        var updated = reg with { Label = "updated" };
        await _store.UpdateAsync(updated);

        var retrieved = await _store.GetAsync(reg.Id);
        Assert.Equal("updated", retrieved!.Label);
    }

    [Fact]
    public async Task UpdateAsync_WithStaleRegistration_PreservesPinnedConversation()
    {
        var staleRegistration = await _store.CreateAsync(MakeRegistration());
        var conversationId = ConversationId.From("conv-update-preserved");
        await _store.TryPinConversationAsync(staleRegistration.Id, conversationId);

        await _store.UpdateAsync(staleRegistration with
        {
            Label = "updated",
            Enabled = false,
            DefaultResponseMode = WebhookResponseMode.Sync
        });

        var retrieved = await _store.GetAsync(staleRegistration.Id);
        retrieved.ShouldNotBeNull();
        retrieved.Label.ShouldBe("updated");
        retrieved.Enabled.ShouldBeFalse();
        retrieved.DefaultResponseMode.ShouldBe(WebhookResponseMode.Sync);
        retrieved.PinnedConversationId.ShouldBe(conversationId);
    }

    [Fact]
    public async Task UpdateAsync_WithStaleSnapshot_PreservesStoreOwnedPinAndLastUsed()
    {
        var staleRegistration = await _store.CreateAsync(MakeRegistration(label: "original"));
        var conversationId = ConversationId.From("conv-concurrent");
        var usedAt = DateTimeOffset.UtcNow;
        await _store.TryPinConversationAsync(staleRegistration.Id, conversationId);
        await _store.TouchLastUsedAsync(staleRegistration.Id, usedAt);

        var updated = await _store.UpdateAsync(staleRegistration with { Label = "updated" });

        updated.Label.ShouldBe("updated");
        updated.PinnedConversationId.ShouldBe(conversationId);
        updated.LastUsedAt.ShouldBe(usedAt);
        var retrieved = await _store.GetAsync(staleRegistration.Id);
        retrieved.ShouldNotBeNull();
        retrieved.PinnedConversationId.ShouldBe(conversationId);
        retrieved.LastUsedAt.ShouldBe(usedAt);
    }

    [Fact]
    public async Task TouchLastUsedAsync_AfterPin_PreservesPinnedConversation()
    {
        var staleRegistration = await _store.CreateAsync(MakeRegistration());
        var conversationId = ConversationId.From("conv-preserved");
        await _store.TryPinConversationAsync(staleRegistration.Id, conversationId);
        var usedAt = DateTimeOffset.UtcNow;

        await _store.TouchLastUsedAsync(staleRegistration.Id, usedAt);

        var retrieved = await _store.GetAsync(staleRegistration.Id);
        retrieved.ShouldNotBeNull();
        retrieved.PinnedConversationId.ShouldBe(conversationId);
        retrieved.LastUsedAt.ShouldBe(usedAt);
    }

    [Fact]
    public async Task Delete_RemovesRecord()
    {
        var reg = await _store.CreateAsync(MakeRegistration());
        await _store.DeleteAsync(reg.Id);

        var retrieved = await _store.GetAsync(reg.Id);
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task TryPinConversationAsync_PinsOnFirstCall()
    {
        var reg = await _store.CreateAsync(MakeRegistration());
        var convId = ConversationId.From("conv-001");

        var pinned = await _store.TryPinConversationAsync(reg.Id, convId);
        Assert.NotNull(pinned);
        Assert.Equal("conv-001", pinned.Value.Value);

        var retrieved = await _store.GetAsync(reg.Id);
        Assert.Equal("conv-001", retrieved!.PinnedConversationId!.Value.Value);
    }

    [Fact]
    public async Task TryPinConversationAsync_CasWinsWithFirst()
    {
        var reg = await _store.CreateAsync(MakeRegistration());
        var first = ConversationId.From("conv-first");
        var second = ConversationId.From("conv-second");

        var pinFirst = await _store.TryPinConversationAsync(reg.Id, first);
        var pinSecond = await _store.TryPinConversationAsync(reg.Id, second);

        // Both calls return the winner — which is "first"
        Assert.Equal("conv-first", pinFirst!.Value.Value);
        Assert.Equal("conv-first", pinSecond!.Value.Value);
    }

    [Fact]
    public async Task TryPinConversationAsync_ReturnsNullForMissingRegistration()
    {
        var result = await _store.TryPinConversationAsync(WebhookId.Create(), ConversationId.From("conv-x"));
        Assert.Null(result);
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        // Second call should not throw
        await _store.InitializeAsync();
        await _store.InitializeAsync();
    }
}
