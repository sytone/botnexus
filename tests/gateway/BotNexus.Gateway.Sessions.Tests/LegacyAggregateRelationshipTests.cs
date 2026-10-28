using System.IO.Abstractions.TestingHelpers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;
namespace BotNexus.Gateway.Sessions.Tests;
public sealed class LegacyAggregateRelationshipTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "legacy-relationships", "sessions"));
    private static readonly string Data = Path.Combine(Path.GetDirectoryName(Root) ?? throw new InvalidOperationException(), "data");
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string At(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    private static void Put<T>(MockFileSystem fs, string path, T value) => fs.AddFile(At(path), new MockFileData(JsonSerializer.Serialize(value, Options)));
    private static void Change(MockFileSystem fs, string path, string property, string? value)
    {
        var json = JsonNode.Parse(fs.File.ReadAllText(At(path))) as JsonObject ?? throw new InvalidOperationException();
        json[property] = value;
        fs.AddFile(At(path), new MockFileData(json.ToJsonString()));
    }
    private static MockFileSystem Fixture()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(Root);
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        foreach (var (session, conversation, agent) in new[] { ("s", "c", "agent"), ("child", "cc", "child-agent") })
        {
            Put(fs, session + ".meta.json", new FileSessionStore.SessionMeta(AgentId.From(agent), null, null, null, null, now, now, ConversationId: ConversationId.From(conversation)));
            fs.AddFile(At(session + ".jsonl"), new MockFileData("{\"role\":\"user\",\"content\":\"private-transcript\"}\r\n"));
            Put(fs, $"conversations/{agent}/{conversation}.json", new Conversation { ConversationId = ConversationId.From(conversation), AgentId = AgentId.From(agent), ActiveSessionId = SessionId.From(session) });
        }
        Put(fs, "subagents/run.json", new SubAgentRunDetail { SubAgentId = "run", ParentSessionId = "s", ParentConversationId = "c", ParentAgentId = "agent", ChildSessionId = "child", ChildConversationId = "cc", ChildAgentId = "child-agent", Status = SubAgentStatus.Completed, ResultSummary = "private-summary" });
        Cursor(fs, "world-one", "reader");
        Cursor(fs, "world-two", "reader");
        return fs;
    }
    private static void Cursor(MockFileSystem fs, string world, string reader, string conversation = "c")
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{world}\0{reader}\0{conversation}")));
        Put(fs, $"conversations/read-state/{key}.json", new { WorldId = world, ReaderId = reader, ConversationId = conversation, Position = 1, Version = 1 });
    }
    private static Task<LegacyInventoryResult> Inspect(MockFileSystem fs, CancellationToken ct = default) =>
        new LegacyAggregateInventory(fs).InspectRelationshipsAsync(Root, Data, ct);
    [Fact]
    public void InspectRelationshipsAsync_PublicOptInExists() => typeof(LegacyAggregateInventory).GetMethod("InspectRelationshipsAsync").ShouldNotBeNull();
    [Fact]
    public async Task InspectRelationshipsAsync_ValidLinks_PreservesExactInventoryAndSource()
    {
        var fs = Fixture();
        var before = fs.AllFiles.ToDictionary(p => p, p => fs.File.ReadAllBytes(p));
        var inventory = await new LegacyAggregateInventory(fs).InspectAsync(Root, Data);
        var result = await Inspect(fs);
        result.Files.ShouldBe(inventory.Files);
        result.RetainedSectionsPath.ShouldBe(inventory.RetainedSectionsPath);
        result.Files.Count.ShouldBe(9);
        JsonSerializer.Serialize(result).ShouldNotContain("private-");
        fs.AllFiles.Order().ShouldBe(before.Keys.Order());
        foreach (var pair in before) fs.File.ReadAllBytes(pair.Key).ShouldBe(pair.Value);
        fs.Directory.Exists(Data).ShouldBeFalse();
    }
    [Theory]
    [InlineData("conversations/agent/c.json", "activeSessionId", "missing")]
    [InlineData("conversations/agent/c.json", "activeSessionId", "child")]
    [InlineData("s.meta.json", "conversationId", null)]
    [InlineData("s.meta.json", "conversationId", "cc")]
    [InlineData("s.meta.json", "agentId", "child-agent")]
    public async Task InspectRelationshipsAsync_InvalidBinding_OnlyOptInRejects(string path, string field, string? value)
    {
        var fs = Fixture();
        Change(fs, path, field, value);
        await new LegacyAggregateInventory(fs).InspectAsync(Root, Data);
        var ex = await Should.ThrowAsync<InvalidDataException>(() => Inspect(fs));
        ex.InnerException.ShouldBeNull();
        ex.Message.ShouldNotContain("private-");
    }
    [Theory]
    [InlineData("parentConversationId", "cc")]
    [InlineData("childConversationId", "c")]
    [InlineData("parentAgentId", "child-agent")]
    [InlineData("childAgentId", "agent")]
    public async Task InspectRelationshipsAsync_RunRecordedConversationAgentConflict_OnlyOptInRejects(string field, string value)
    {
        var fs = Fixture();
        Change(fs, "subagents/run.json", field, value);
        await new LegacyAggregateInventory(fs).InspectAsync(Root, Data);
        await Should.ThrowAsync<InvalidDataException>(() => Inspect(fs));
    }
    [Theory]
    [InlineData("s.meta.json", "parentAgentId", "wrong")]
    [InlineData("child.meta.json", "childAgentId", "wrong")]
    public async Task InspectRelationshipsAsync_RunWithoutRecordedConversation_DoesNotInferCurrentBinding(string meta, string field, string value)
    {
        var fs = Fixture();
        Change(fs, "subagents/run.json", field.StartsWith("parent", StringComparison.Ordinal) ? "parentConversationId" : "childConversationId", null);
        Change(fs, "subagents/run.json", field, value);
        Change(fs, meta, "agentId", null);
        // A retained agent without a recorded conversation cannot be checked against a
        // session's current binding: that session may have been rebound since the run.
        await Inspect(fs);
    }
    [Theory]
    [InlineData("parentSessionId", "child")]
    [InlineData("childSessionId", "s")]
    public async Task InspectRelationshipsAsync_RunSessionIdentityDoesNotConstrainCurrentBinding(string field, string value)
    {
        var fs = Fixture();
        Change(fs, "subagents/run.json", field, value);
        // Session identifiers on retained runs are historical, not foreign keys to
        // the current metadata binding. Only recorded conversation/agent pairs agree.
        await Inspect(fs);
    }
    [Theory]
    [InlineData("s", "c", "agent", "agent")]
    [InlineData("child", "cc", "child-agent", "child-agent")]
    [InlineData("s", "c", "agent", "rebound-agent")]
    [InlineData("child", "cc", "child-agent", "rebound-agent")]
    public async Task InspectRelationshipsAsync_ReboundSession_RetainsHistoricalRunIdentity(
        string session, string originalConversation, string originalAgent, string currentAgent)
    {
        var fs = Fixture();
        var runBefore = fs.File.ReadAllBytes(At("subagents/run.json"));
        var originalPath = $"conversations/{originalAgent}/{originalConversation}.json";
        // Model SessionStoreBase.RebindSessionsAsync/SaveAsync: change the same
        // session's binding, keep the old conversation and run, adjust active links.
        Change(fs, originalPath, "activeSessionId", null);
        Change(fs, session + ".meta.json", "conversationId", "rebound");
        Change(fs, session + ".meta.json", "agentId", currentAgent);
        Put(fs, $"conversations/{currentAgent}/rebound.json", new Conversation
        {
            ConversationId = ConversationId.From("rebound"),
            AgentId = AgentId.From(currentAgent),
            ActiveSessionId = SessionId.From(session)
        });
        var inventory = await new LegacyAggregateInventory(fs).InspectAsync(Root, Data);
        var strict = await Inspect(fs);
        strict.Files.ShouldBe(inventory.Files);
        fs.File.ReadAllBytes(At("subagents/run.json")).ShouldBe(runBefore);
    }
    [Theory]
    [InlineData("parentConversationId", "parentAgentId")]
    [InlineData("childConversationId", "childAgentId")]
    public async Task InspectRelationshipsAsync_MissingRecordedConversation_DoesNotUseCurrentSessionAgent(
        string conversationField, string agentField)
    {
        var fs = Fixture();
        Change(fs, "subagents/run.json", conversationField, "cleaned-up-conversation");
        Change(fs, "subagents/run.json", agentField, "historical-agent");
        await Inspect(fs);
    }
    [Theory]
    [InlineData("parentSessionId", "parentAgentId")]
    [InlineData("childSessionId", "childAgentId")]
    public async Task InspectRelationshipsAsync_RecordedConversationAgentConflict_RejectsWithoutSessionTarget(
        string sessionField, string agentField)
    {
        var fs = Fixture();
        Change(fs, "subagents/run.json", sessionField, "cleaned-up-session");
        Change(fs, "subagents/run.json", agentField, "wrong");
        await new LegacyAggregateInventory(fs).InspectAsync(Root, Data);
        var ex = await Should.ThrowAsync<InvalidDataException>(() => Inspect(fs));
        ex.InnerException.ShouldBeNull();
        ex.Message.ShouldNotContain("private-");
    }
    [Fact]
    public async Task InspectRelationshipsAsync_DanglingCursor_OnlyOptInRejects()
    {
        var fs = Fixture();
        Cursor(fs, "remote-world", "reader", "missing");
        await new LegacyAggregateInventory(fs).InspectAsync(Root, Data);
        await Should.ThrowAsync<InvalidDataException>(() => Inspect(fs));
    }
    [Fact]
    public async Task InspectRelationshipsAsync_ArchivedPairCannotSatisfyActiveSession()
    {
        var fs = Fixture();
        foreach (var ext in new[] { ".meta.json", ".jsonl" })
        {
            fs.AddFile(At("s" + ext + ".archived.20260101000000"), new MockFileData(fs.File.ReadAllBytes(At("s" + ext))));
            fs.RemoveFile(At("s" + ext));
        }
        await new LegacyAggregateInventory(fs).InspectAsync(Root, Data);
        await Should.ThrowAsync<InvalidDataException>(() => Inspect(fs));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectRelationshipsAsync_DeletedOrArchivedRunSessions_AreAllowed(bool archive)
    {
        var fs = Fixture();
        foreach (var session in new[] { "s", "child" })
            foreach (var ext in new[] { ".meta.json", ".jsonl" })
            {
                if (archive) fs.AddFile(At(session + ext + ".archived.20260101000000"), new MockFileData(fs.File.ReadAllBytes(At(session + ext))));
                fs.RemoveFile(At(session + ext));
            }
        Change(fs, "conversations/agent/c.json", "activeSessionId", null);
        Change(fs, "conversations/child-agent/cc.json", "activeSessionId", null);
        await Inspect(fs);
    }
    [Fact]
    public async Task InspectRelationshipsAsync_MissingRetainedConversationsAndParentLink_AreNotInvented()
    {
        var fs = Fixture();
        Change(fs, "subagents/run.json", "parentSessionId", "deleted-parent");
        Change(fs, "subagents/run.json", "childSessionId", "deleted-child");
        Change(fs, "subagents/run.json", "parentConversationId", "deleted-conversation");
        Change(fs, "subagents/run.json", "childConversationId", "deleted-child-conversation");
        Change(fs, "conversations/agent/c.json", "parentConversationId", "deleted-ancestor");
        await Inspect(fs);
    }
    [Fact]
    public async Task InspectRelationshipsAsync_NullLegacyAgentAndAbsentActiveLink_AreAllowed()
    {
        var fs = Fixture();
        Change(fs, "s.meta.json", "agentId", null);
        Change(fs, "conversations/agent/c.json", "activeSessionId", null);
        await Inspect(fs);
    }
    [Fact]
    public async Task InspectRelationshipsAsync_ArchivedExplicitAgentConflict_Rejects()
    {
        var fs = Fixture();
        fs.AddFile(At("old.jsonl.archived.20260101000000"), new MockFileData(""));
        fs.AddFile(At("old.meta.json.archived.20260101000000"), new MockFileData(fs.File.ReadAllBytes(At("s.meta.json"))));
        Change(fs, "old.meta.json.archived.20260101000000", "agentId", "wrong");
        await Should.ThrowAsync<InvalidDataException>(() => Inspect(fs));
    }
    [Fact]
    public async Task InspectRelationshipsAsync_MalformedHistory_StillFailsClosed()
    {
        var fs = Fixture();
        fs.AddFile(At("s.jsonl"), new MockFileData("{\"role\":\"user\",\"content\":\"x\",\"unexpected\":true}\n"));
        await Should.ThrowAsync<InvalidDataException>(() => Inspect(fs));
    }
    [Fact]
    public async Task InspectRelationshipsAsync_Cancelled_DoesNotReturnInventory()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => Inspect(Fixture(), cancellation.Token));
    }
}
