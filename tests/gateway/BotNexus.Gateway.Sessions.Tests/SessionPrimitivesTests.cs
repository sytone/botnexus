using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Sessions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Sessions.Tests;

public sealed class SessionPrimitivesTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void SessionFileNames_SanitizesUnsafeSessionIds()
    {
        var sanitized = SessionFileNames.SanitizeSessionId("session/日本語?:*&%20");

        sanitized.ShouldBe("session%2F%E6%97%A5%E6%9C%AC%E8%AA%9E%3F%3A%2A%26%2520");
        SessionFileNames.HistoryFileName("id").ShouldBe("id.jsonl");
        SessionFileNames.MetadataFileName("id").ShouldBe("id.meta.json");
    }

    [Fact]
    public async Task SessionJsonl_WriteAndRead_RoundTripsEntries()
    {
        var fileSystem = new MockFileSystem();
        var path = Path.Combine(Path.GetTempPath(), "sessions", "history.jsonl");
        var entries = new[]
        {
            new SessionEntry { Role = MessageRole.User, Content = "hello" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "world", IsCompactionSummary = true }
        };

        await SessionJsonl.WriteAllAsync(fileSystem, path, entries, JsonOptions);
        var read = await SessionJsonl.ReadAllAsync<SessionEntry>(fileSystem, path, JsonOptions);

        read.Count().ShouldBe(2);
        read[0].Content.ShouldBe("hello");
        read[1].IsCompactionSummary.ShouldBeTrue();
    }

    [Fact]
    public async Task SessionMetadataSidecar_WriteAndRead_RoundTripsMetadata()
    {
        var fileSystem = new MockFileSystem();
        var path = Path.Combine(Path.GetTempPath(), "sessions", "session.meta.json");
        var metadata = new TestMetadata("s1", "agent-a", DateTimeOffset.UtcNow);

        await SessionMetadataSidecar.WriteAsync(fileSystem, path, metadata, JsonOptions);
        var read = await SessionMetadataSidecar.ReadAsync<TestMetadata>(fileSystem, path, JsonOptions);

        read.ShouldBe(metadata);
    }

    [Fact]
    public void SessionCompaction_ApplyLegacyHistoryProjection_NoSummaries_ReturnsUnchanged()
    {
        var entries = new[]
        {
            new SessionEntry { Role = MessageRole.User, Content = "one" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "two" }
        };

        var projected = SessionCompaction.ApplyLegacyHistoryProjection(entries);

        projected.Count.ShouldBe(2);
        projected.ShouldAllBe(e => !e.IsHistory);
    }

    [Fact]
    public void SessionCompaction_ApplyLegacyHistoryProjection_SingleSummary_ReturnsUnchanged()
    {
        var entries = new[]
        {
            new SessionEntry { Role = MessageRole.User, Content = "one" },
            new SessionEntry { Role = MessageRole.System, Content = "summary", IsCompactionSummary = true },
            new SessionEntry { Role = MessageRole.User, Content = "two" }
        };

        var projected = SessionCompaction.ApplyLegacyHistoryProjection(entries);

        projected.Count.ShouldBe(3);
        projected.ShouldAllBe(e => !e.IsHistory);
    }

    [Fact]
    public void SessionCompaction_ApplyLegacyHistoryProjection_MultipleSummaries_MarksAllButLatestAsHistory()
    {
        var entries = new[]
        {
            new SessionEntry { Role = MessageRole.User, Content = "before-1" },
            new SessionEntry { Role = MessageRole.System, Content = "summary-1", IsCompactionSummary = true },
            new SessionEntry { Role = MessageRole.User, Content = "between" },
            new SessionEntry { Role = MessageRole.System, Content = "summary-2", IsCompactionSummary = true },
            new SessionEntry { Role = MessageRole.Assistant, Content = "after" }
        };

        var projected = SessionCompaction.ApplyLegacyHistoryProjection(entries);

        projected.Count.ShouldBe(5);
        projected[0].IsHistory.ShouldBeFalse();
        projected[1].IsCompactionSummary.ShouldBeTrue();
        projected[1].IsHistory.ShouldBeTrue();
        projected[2].IsHistory.ShouldBeFalse();
        projected[3].IsCompactionSummary.ShouldBeTrue();
        projected[3].IsHistory.ShouldBeFalse();
        projected[4].IsHistory.ShouldBeFalse();
    }

    [Fact]
    public void SessionCompaction_ApplyLegacyHistoryProjection_AlreadyMigrated_IsIdempotent()
    {
        var entries = new[]
        {
            new SessionEntry { Role = MessageRole.System, Content = "summary-1", IsCompactionSummary = true, IsHistory = true },
            new SessionEntry { Role = MessageRole.User, Content = "between" },
            new SessionEntry { Role = MessageRole.System, Content = "summary-2", IsCompactionSummary = true },
            new SessionEntry { Role = MessageRole.User, Content = "after" }
        };

        var projected = SessionCompaction.ApplyLegacyHistoryProjection(entries);

        projected.Count.ShouldBe(4);
        projected[0].IsHistory.ShouldBeTrue();
        projected[2].IsHistory.ShouldBeFalse();
    }

    private sealed record TestMetadata(string SessionId, string AgentId, DateTimeOffset UpdatedAt);
}
