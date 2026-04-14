using BotNexus.Probe.LogIngestion;
using FluentAssertions;
using System.Text.Json;

namespace BotNexus.Probe.Tests;

public sealed class JsonlSessionReaderTests
{
    private readonly JsonlSessionReader _reader = new();

    [Fact]
    public async Task ReadAsync_ValidJsonl_YieldsDocumentsPerLine()
    {
        using var temp = new TestTempDirectory();
        temp.WriteFile("session.jsonl", """
{"sessionId":"s1","role":"user","content":"hello"}
{"sessionId":"s1","role":"assistant","content":"hi"}
""");

        var docs = await CollectDocumentsAsync(_reader.ReadAsync(temp.File("session.jsonl")));

        docs.Should().HaveCount(2);
        docs[0].RootElement.GetProperty("role").GetString().Should().Be("user");
    }

    [Fact]
    public async Task ReadAsync_MultipleLines_ParsesAllValidLines()
    {
        using var temp = new TestTempDirectory();
        temp.WriteFile("multi.jsonl", """
{"sessionId":"s1","content":"1"}
{"sessionId":"s1","content":"2"}
{"sessionId":"s1","content":"3"}
""");

        var docs = await CollectDocumentsAsync(_reader.ReadAsync(temp.File("multi.jsonl")));

        docs.Should().HaveCount(3);
    }

    [Fact]
    public async Task ReadAsync_MalformedLine_SkipsAndWarns()
    {
        using var temp = new TestTempDirectory();
        temp.WriteFile("broken.jsonl", """
{"sessionId":"s1","content":"ok"}
not-json
{"sessionId":"s1","content":"still-ok"}
""");
        var warnings = new List<string>();

        var docs = await CollectDocumentsAsync(_reader.ReadAsync(temp.File("broken.jsonl"), warning: warnings.Add));

        docs.Should().HaveCount(2);
        warnings.Should().ContainSingle();
        warnings[0].Should().Contain("Skipping malformed JSONL line 2");
    }

    [Fact]
    public async Task ReadAsync_EmptyFile_ReturnsNoDocuments()
    {
        using var temp = new TestTempDirectory();
        temp.WriteFile("empty.jsonl", string.Empty);

        var docs = await CollectDocumentsAsync(_reader.ReadAsync(temp.File("empty.jsonl")));

        docs.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_SupportsSkipAndTakePagination()
    {
        using var temp = new TestTempDirectory();
        temp.WriteFile("paging.jsonl", """
{"n":1}
{"n":2}
{"n":3}
{"n":4}
""");

        var docs = await CollectDocumentsAsync(_reader.ReadAsync(temp.File("paging.jsonl"), skip: 1, take: 2));

        docs.Select(d => d.RootElement.GetProperty("n").GetInt32()).Should().Equal(2, 3);
    }

    [Fact]
    public async Task ReadMessagesAsync_ExtractsCommonFields()
    {
        using var temp = new TestTempDirectory();
        temp.WriteFile("messages.jsonl", """
{"sessionId":"s-42","timestamp":"2025-02-01T10:00:00Z","role":"assistant","content":"hello","agentId":"a-1"}
""");

        var messages = await CollectMessagesAsync(_reader.ReadMessagesAsync(temp.File("messages.jsonl")));

        messages.Should().ContainSingle();
        var message = messages[0];
        message.SessionId.Should().Be("s-42");
        message.Role.Should().Be("assistant");
        message.Content.Should().Be("hello");
        message.AgentId.Should().Be("a-1");
        message.Timestamp.Should().Be(DateTimeOffset.Parse("2025-02-01T10:00:00Z"));
    }

    [Fact]
    public void ToSessionMessage_VaryingSchemas_HandlesMissingFieldsAndFallbacks()
    {
        using var document = JsonDocument.Parse("""
{"session_id":"legacy","message":"hello","agent_id":"legacy-agent","extra":"x"}
""");

        var message = JsonlSessionReader.ToSessionMessage(document.RootElement, "fallback-session");

        message.SessionId.Should().Be("legacy");
        message.Content.Should().Be("hello");
        message.AgentId.Should().Be("legacy-agent");
        message.Role.Should().BeNull();
        message.Metadata.Should().NotBeNull();
        message.Metadata!.Should().ContainKey("extra");
    }

    [Fact]
    public async Task ReadMessagesAsync_WhenSessionIdMissing_UsesFileNameFallback()
    {
        using var temp = new TestTempDirectory();
        temp.WriteFile("fallback-session.jsonl", """{"content":"orphan"}""");

        var messages = await CollectMessagesAsync(_reader.ReadMessagesAsync(temp.File("fallback-session.jsonl")));

        messages.Should().ContainSingle();
        messages[0].SessionId.Should().Be("fallback-session");
    }

    private static async Task<List<JsonDocument>> CollectDocumentsAsync(IAsyncEnumerable<JsonDocument> source)
    {
        var list = new List<JsonDocument>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    private static async Task<List<SessionMessage>> CollectMessagesAsync(IAsyncEnumerable<SessionMessage> source)
    {
        var list = new List<SessionMessage>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
