using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Sessions.Common;
using FluentAssertions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Sessions.Common.Tests;

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

        sanitized.Should().Be("session%2F%E6%97%A5%E6%9C%AC%E8%AA%9E%3F%3A%2A%26%2520");
        SessionFileNames.HistoryFileName("id").Should().Be("id.jsonl");
        SessionFileNames.MetadataFileName("id").Should().Be("id.meta.json");
    }

    [Fact]
    public async Task SessionJsonl_WriteAndRead_RoundTripsEntries()
    {
        var fileSystem = new MockFileSystem();
        var path = @"C:\sessions\history.jsonl";
        var entries = new[]
        {
            new SessionEntry { Role = MessageRole.User, Content = "hello" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "world", IsCompactionSummary = true }
        };

        await SessionJsonl.WriteAllAsync(fileSystem, path, entries, JsonOptions);
        var read = await SessionJsonl.ReadAllAsync<SessionEntry>(fileSystem, path, JsonOptions);

        read.Should().HaveCount(2);
        read[0].Content.Should().Be("hello");
        read[1].IsCompactionSummary.Should().BeTrue();
    }

    [Fact]
    public async Task SessionMetadataSidecar_WriteAndRead_RoundTripsMetadata()
    {
        var fileSystem = new MockFileSystem();
        var path = @"C:\sessions\session.meta.json";
        var metadata = new TestMetadata("s1", "agent-a", DateTimeOffset.UtcNow);

        await SessionMetadataSidecar.WriteAsync(fileSystem, path, metadata, JsonOptions);
        var read = await SessionMetadataSidecar.ReadAsync<TestMetadata>(fileSystem, path, JsonOptions);

        read.Should().Be(metadata);
    }

    [Fact]
    public void SessionCompaction_KeepFromLastCompaction_ReturnsSuffix()
    {
        var entries = new[]
        {
            new SessionEntry { Role = MessageRole.User, Content = "one" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "summary", IsCompactionSummary = true },
            new SessionEntry { Role = MessageRole.User, Content = "two" }
        };

        var compacted = SessionCompaction.KeepFromLastCompaction(entries);

        compacted.Should().HaveCount(2);
        compacted[0].IsCompactionSummary.Should().BeTrue();
        compacted[1].Content.Should().Be("two");
    }

    private sealed record TestMetadata(string SessionId, string AgentId, DateTimeOffset UpdatedAt);
}
