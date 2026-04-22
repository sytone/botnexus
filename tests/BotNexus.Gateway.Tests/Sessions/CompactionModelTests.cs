using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class CompactionModelTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void CompactionOptions_DefaultValues_AreReasonable()
    {
        var options = new CompactionOptions();

        options.PreservedTurns.ShouldBe(3);
        options.MaxSummaryChars.ShouldBe(16_000);
        options.TokenThresholdRatio.ShouldBe(0.6);
        options.ContextWindowTokens.ShouldBe(128_000);
        options.SummarizationModel.ShouldBeNull();
    }

    [Fact]
    public void GatewaySession_ReplaceHistory_ClearsAndSetsNewEntries()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-a"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        session.AddEntries([
            new SessionEntry { Role = MessageRole.User, Content = "old-1" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "old-2" }
        ]);

        session.ReplaceHistory([
            new SessionEntry { Role = MessageRole.System, Content = "summary", IsCompactionSummary = true },
            new SessionEntry { Role = MessageRole.User, Content = "new-1" }
        ]);

        session.GetHistorySnapshot().Select(entry => entry.Content)
            .ShouldBe(new[] { "summary", "new-1" }, ignoreOrder: false);
    }

    [Fact]
    public void GatewaySession_ReplaceHistory_UpdatesTimestamp()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-a"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var initialUpdatedAt = session.UpdatedAt;
        Thread.Sleep(10);

        session.ReplaceHistory([new SessionEntry { Role = MessageRole.User, Content = "new-content" }]);

        session.UpdatedAt.ShouldBeGreaterThan(initialUpdatedAt);
    }

    [Fact]
    public void SessionEntry_IsCompactionSummary_DefaultFalse()
    {
        var entry = new SessionEntry { Role = MessageRole.User, Content = "hello" };

        entry.IsCompactionSummary.ShouldBeFalse();
    }

    [Fact]
    public void SessionEntry_IsCompactionSummary_SerializesCorrectly()
    {
        var original = new SessionEntry
        {
            Role = MessageRole.System,
            Content = "compacted",
            IsCompactionSummary = true
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<SessionEntry>(json, JsonOptions);

        roundTrip.ShouldNotBeNull();
        roundTrip!.IsCompactionSummary.ShouldBeTrue();
    }

    [Fact]
    public async Task FileSessionStore_LoadWithCompactionEntry_SkipsBefore()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.ReplaceHistory([
            new SessionEntry { Role = MessageRole.User, Content = "before-1" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "before-2" },
            new SessionEntry { Role = MessageRole.System, Content = "summary", IsCompactionSummary = true },
            new SessionEntry { Role = MessageRole.User, Content = "after-1" }
        ]);
        await store.SaveAsync(session);

        var reloaded = await fixture.CreateStore().GetAsync("s1");

        reloaded.ShouldNotBeNull();
        reloaded!.GetHistorySnapshot().Select(entry => entry.Content)
            .ShouldBe(new[] { "summary", "after-1" }, ignoreOrder: false);
    }

    [Fact]
    public async Task FileSessionStore_LoadWithMultipleCompactions_UsesLastOne()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.ReplaceHistory([
            new SessionEntry { Role = MessageRole.User, Content = "before-1" },
            new SessionEntry { Role = MessageRole.System, Content = "summary-1", IsCompactionSummary = true },
            new SessionEntry { Role = MessageRole.User, Content = "between" },
            new SessionEntry { Role = MessageRole.System, Content = "summary-2", IsCompactionSummary = true },
            new SessionEntry { Role = MessageRole.Assistant, Content = "after" }
        ]);
        await store.SaveAsync(session);

        var reloaded = await fixture.CreateStore().GetAsync("s1");

        reloaded.ShouldNotBeNull();
        reloaded!.GetHistorySnapshot().Select(entry => entry.Content)
            .ShouldBe(new[] { "summary-2", "after" }, ignoreOrder: false);
    }

    [Fact]
    public async Task FileSessionStore_LoadWithNoCompaction_ReturnsAllEntries()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.ReplaceHistory([
            new SessionEntry { Role = MessageRole.User, Content = "one" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "two" }
        ]);
        await store.SaveAsync(session);

        var reloaded = await fixture.CreateStore().GetAsync("s1");

        reloaded.ShouldNotBeNull();
        reloaded!.GetHistorySnapshot().Select(entry => entry.Content)
            .ShouldBe(new[] { "one", "two" }, ignoreOrder: false);
    }

    private sealed class StoreFixture : IDisposable
    {
        public StoreFixture()
        {
            FileSystem = new MockFileSystem();
            StorePath = Path.Combine(Path.GetTempPath(), "CompactionModelTests", Guid.NewGuid().ToString("N"));
            FileSystem.Directory.CreateDirectory(StorePath);
        }

        public MockFileSystem FileSystem { get; }
        public string StorePath { get; }

        public FileSessionStore CreateStore()
            => new(StorePath, NullLogger<FileSessionStore>.Instance, FileSystem);

        public void Dispose()
        {
            if (FileSystem.Directory.Exists(StorePath))
                FileSystem.Directory.Delete(StorePath, true);
        }
    }
}

