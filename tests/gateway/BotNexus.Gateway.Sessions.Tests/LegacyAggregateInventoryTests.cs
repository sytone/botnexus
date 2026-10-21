using System.IO.Abstractions.TestingHelpers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Sessions.Tests;

public sealed class LegacyAggregateInventoryTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "legacy-inventory-fixture", "sessions"));
    private static readonly string Data = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "legacy-inventory-fixture", "data"));
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string At(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Options);
    private static MockFileSystem Fixture()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(Root);
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        fs.AddFile(At("s.meta.json"), new MockFileData(Json(new FileSessionStore.SessionMeta(
            AgentId.From("agent"), null, null, null, null, now, now, ConversationId: ConversationId.From("c")))));
        fs.AddFile(At("s.jsonl"), new MockFileData(Json(new SessionEntry { Role = MessageRole.User, Content = "private-first", Timestamp = now }) + "\n" +
            Json(new SessionEntry { Role = MessageRole.Assistant, Content = "private-second", Timestamp = now }) + "\n"));
        fs.AddFile(At("conversations/agent/c.json"), new MockFileData(Json(new Conversation { ConversationId = ConversationId.From("c"), AgentId = AgentId.From("agent"), CreatedAt = now, UpdatedAt = now })));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("world\0reader\0c")));
        fs.AddFile(At($"conversations/read-state/{hash}.json"), new MockFileData(Json(new ConversationReadState("world", ConversationReaderId.From("reader"), ConversationId.From("c"), ConversationReadPosition.From(1), 1))));
        fs.AddFile(At("subagents/run.json"), new MockFileData(Json(new SubAgentRunDetail { SubAgentId = "run", ParentSessionId = "s", SpawningToolCallId = "call", Status = SubAgentStatus.Completed })));
        fs.AddFile(At("subagent-spawns/s/call.json"), new MockFileData(Json("run")));
        fs.AddFile(Path.Combine(Data, "sections.sqlite"), new MockFileData("not sqlite"));
        return fs;
    }

    [Fact]
    public async Task InspectAsync_ValidAggregate_ReportsHashesCountsAndNeverMutates()
    {
        var fs = Fixture();
        var before = fs.AllFiles.ToDictionary(p => p, p => fs.File.ReadAllBytes(p));
        var result = await new LegacyAggregateInventory(fs).InspectAsync(Root, Data);
        result.Files.Count.ShouldBe(6);
        result.Files.Sum(f => f.RecordCount).ShouldBe(7);
        result.Files.Select(f => f.Category).Distinct().Count().ShouldBe(6);
        result.RetainedSectionsPath.ShouldBe(Path.Combine(Data, "sections.sqlite"));
        foreach (var file in result.Files)
        {
            var bytes = before[At(file.RelativePath)];
            file.ByteLength.ShouldBe(bytes.LongLength);
            file.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        JsonSerializer.Serialize(result).ShouldNotContain("private-first");
        JsonSerializer.Serialize(result).ShouldNotContain("private-second");
        fs.AllFiles.Order().ShouldBe(before.Keys.Order());
        foreach (var pair in before) fs.File.ReadAllBytes(pair.Key).ShouldBe(pair.Value);
    }

    [Theory]
    [InlineData("s.jsonl", "null\n")]
    [InlineData("s.jsonl", "{\"role\":\"user\",\"content\":\"x\"")]
    [InlineData("s.jsonl", "[]\n")]
    [InlineData("s.jsonl", "{\"role\":\"user\",\"content\":null}\n")]
    [InlineData("s.jsonl", "{\"role\":\"user\",\"content\":\"x\",\"future\":1}\n")]
    [InlineData("s.jsonl", "{\"role\":\"user\",\"role\":\"assistant\",\"content\":\"x\"}\n")]
    [InlineData("s.meta.json", "null")]
    [InlineData("s.meta.json", "{}")]
    [InlineData("s.meta.json", "{\"sessionId\":\"different\"}")]
    [InlineData("conversations/agent/c.json", "null")]
    [InlineData("conversations/agent/c.json", "{\"conversationId\":\"other\",\"agentId\":\"agent\"}")]
    [InlineData("conversations/agent/c.json", "{\"conversationId\":\"c\",\"agentId\":\"other\"}")]
    [InlineData("subagents/run.json", "{\"subAgentId\":\"other\",\"status\":\"Completed\"}")]
    [InlineData("subagent-spawns/s/call.json", "\"missing\"")]
    [InlineData("subagent-spawns/s/call.json", "null")]
    [InlineData("s.jsonl", "{\"role\":0,\"content\":\"x\"}\n")]
    [InlineData("s.jsonl", "{\"role\":\"user\",\"content\":\"x\",\"subAgentConsumptionReceipt\":\"run\"}\n")]
    [InlineData("s.jsonl", "{\"role\":\"user\",\"content\":\"x\",\"originalContentParts\":[{\"future\":1}]}\n")]
    [InlineData("s.meta.json", "{\"createdAt\":\"not a date\",\"updatedAt\":\"2026-01-01T00:00:00Z\",\"agentId\":\"agent\"}")]
    public async Task InspectAsync_InvalidSource_FailsClosed(string path, string json)
    {
        var fs = Fixture();
        fs.AddFile(At(path), new MockFileData(json));
        await Should.ThrowAsync<InvalidDataException>(() => new LegacyAggregateInventory(fs).InspectAsync(Root, Data));
    }

    [Theory]
    [InlineData("s.meta.json")]
    [InlineData("s.jsonl")]
    public async Task InspectAsync_UnpairedSession_FailsClosed(string missing)
    {
        var fs = Fixture();
        fs.RemoveFile(At(missing));
        await Should.ThrowAsync<InvalidDataException>(() => new LegacyAggregateInventory(fs).InspectAsync(Root, Data));
    }

    [Theory]
    [InlineData("unknown.bin", "x")]
    [InlineData("s.meta.json.tmp", "{}")]
    [InlineData("unknown/x.json", "{}")]
    [InlineData("conversations/read-state/wrong.json", "{\"worldId\":\"world\",\"readerId\":\"reader\",\"conversationId\":\"c\",\"position\":1,\"version\":1}")]
    [InlineData("conversations/other/c.json", "{\"conversationId\":\"c\",\"agentId\":\"other\"}")]
    [InlineData("subagents/duplicate.json", "{\"subAgentId\":\"duplicate\",\"parentSessionId\":\"s\",\"spawningToolCallId\":\"call\",\"status\":\"Completed\"}")]
    [InlineData("s.jsonl.archived.invalid", "")]
    [InlineData("s.jsonl.archived.20260101000000", "")]
    public async Task InspectAsync_UnsupportedOrDuplicateSource_FailsClosed(string path, string content)
    {
        var fs = Fixture();
        fs.AddFile(At(path), new MockFileData(content));
        await Should.ThrowAsync<InvalidDataException>(() => new LegacyAggregateInventory(fs).InspectAsync(Root, Data));
    }

    [Fact]
    public async Task InspectAsync_ArchivedPairsAndConsumptionReceipt_AreIncluded()
    {
        var fs = Fixture();
        foreach (var name in new[] { "s.meta.json", "s.jsonl" })
            fs.AddFile(At(name + ".archived.20260101000000"), new MockFileData(fs.File.ReadAllBytes(At(name))));
        var receipt = JsonSerializer.SerializeToNode(new SessionEntry { Role = MessageRole.Tool, Kind = MessageKind.ToolResult, ToolName = "spawn_subagent", ToolCallId = "call", Content = "private-result" }, Options) as JsonObject ?? throw new InvalidOperationException();
        receipt["subAgentConsumptionReceipt"] = "run";
        fs.AddFile(At("s.jsonl"), new MockFileData(receipt.ToJsonString() + "\n"));
        var result = await new LegacyAggregateInventory(fs).InspectAsync(Root, Data);
        result.Files.Count.ShouldBe(8);
        result.Files.Sum(f => f.RecordCount).ShouldBe(9);
        JsonSerializer.Serialize(result).ShouldNotContain("private-result");
    }

    [Fact]
    public async Task InspectAsync_RecordLimit_FailsClosed()
    {
        var fs = Fixture();
        fs.AddFile(At("s.jsonl"), new MockFileData(new string('x', LegacyAggregateInventory.MaxRecordBytes + 1)));
        await Should.ThrowAsync<InvalidDataException>(() => new LegacyAggregateInventory(fs).InspectAsync(Root, Data));
    }

    [Fact]
    public async Task InspectAsync_Cancelled_DoesNotReturnPartialInventory()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => new LegacyAggregateInventory(Fixture()).InspectAsync(Root, Data, cancellation.Token));
    }

    [Theory]
    [InlineData("{\"role\":null,\"content\":\"x\"}\n")]
    [InlineData("{\"role\":\"user\",\"content\":\"x\",\"timestamp\":null}\n")]
    [InlineData("{\"role\":\"user\",\"content\":\"x\"}\n\n")]
    [InlineData("{\"role\":\"user\",\"content\":\"x\",\"future\":{\"key\":1,\"key\":2}}\n")]
    public async Task InspectAsync_InvalidHistoryShapes_FailClosed(string json)
    {
        var fs = Fixture();
        fs.AddFile(At("s.jsonl"), new MockFileData(json));
        await Should.ThrowAsync<InvalidDataException>(() => new LegacyAggregateInventory(fs).InspectAsync(Root, Data));
    }

    [Fact]
    public async Task InspectAsync_EmptyHistoryAndMissingDataDirectory_AreReadOnly()
    {
        var fs = Fixture();
        fs.AddFile(At("s.jsonl"), new MockFileData(""));
        fs.RemoveFile(Path.Combine(Data, "sections.sqlite"));
        fs.Directory.Delete(Data);
        var result = await new LegacyAggregateInventory(fs).InspectAsync(Root, Data);
        result.Files.Single(f => f.RelativePath == "s.jsonl").RecordCount.ShouldBe(0);
        fs.Directory.Exists(Data).ShouldBeFalse();
    }

    [Fact]
    public async Task InspectAsync_MissingConversation_FailsClosed()
    {
        var fs = Fixture();
        fs.RemoveFile(At("conversations/agent/c.json"));
        await Should.ThrowAsync<InvalidDataException>(() => new LegacyAggregateInventory(fs).InspectAsync(Root, Data));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("")]
    public async Task InspectAsync_MultiBufferUtf8Record_HashesExactBytes(string ending)
    {
        var fs = Fixture();
        var content = string.Concat(Enumerable.Repeat("private-😀", 20000));
        var bytes = Encoding.UTF8.GetBytes(Json(new SessionEntry { Role = MessageRole.User, Content = content }) + ending);
        fs.AddFile(At("s.jsonl"), new MockFileData(bytes));
        var result = await new LegacyAggregateInventory(fs).InspectAsync(Root, Data);
        var history = result.Files.Single(f => f.RelativePath == "s.jsonl");
        history.RecordCount.ShouldBe(1);
        history.ByteLength.ShouldBe(bytes.LongLength);
        history.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Theory]
    [InlineData("s.archived.note", false)]
    [InlineData("s.archived.20260101000000", false)]
    [InlineData("s.archived.note", true)]
    public async Task InspectAsync_ArchiveMarkerInsideSessionId_IsNotAnArchiveSuffix(string id, bool archived)
    {
        var fs = Fixture();
        var suffix = archived ? ".archived.20260101000000" : "";
        fs.AddFile(At(SessionFileNames.HistoryFileName(id) + suffix), new MockFileData(fs.File.ReadAllBytes(At("s.jsonl"))));
        fs.AddFile(At(SessionFileNames.MetadataFileName(id) + suffix), new MockFileData(fs.File.ReadAllBytes(At("s.meta.json"))));
        var result = await new LegacyAggregateInventory(fs).InspectAsync(Root, Data);
        result.Files.Count.ShouldBe(8);
        result.Files.Sum(f => f.RecordCount).ShouldBe(10);
    }

    [Fact]
    public async Task InspectAsync_LinkedSource_FailsClosed()
    {
        var fs = Fixture();
        fs.File.SetAttributes(At("s.jsonl"), FileAttributes.ReparsePoint);
        await Should.ThrowAsync<InvalidDataException>(() => new LegacyAggregateInventory(fs).InspectAsync(Root, Data));
    }

    [Fact]
    public async Task InspectAsync_MissingRoot_DoesNotCreateIt()
    {
        var fs = new MockFileSystem();
        await Should.ThrowAsync<DirectoryNotFoundException>(() => new LegacyAggregateInventory(fs).InspectAsync(Root, Data));
        fs.Directory.Exists(Root).ShouldBeFalse();
    }
}
