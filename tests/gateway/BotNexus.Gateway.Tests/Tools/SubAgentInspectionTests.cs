using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class SubAgentInspectionTests
{
    private static readonly SessionId Parent = SessionId.From("parent-session");
    private static readonly SessionId Child = SessionId.From("child-session");
    private const string Summary = "final-result-";

    [Fact]
    public async Task Result_ColdFileManager_PagesFullSummaryRepeatablyWithoutConsumption()
    {
        var fs = new MockFileSystem();
        var conversations = new InMemoryConversationStore();
        var root = Path.Combine(Path.GetTempPath(), "inspection-tests");
        FileSessionStore Store() => new(root, NullLogger<FileSessionStore>.Instance, fs, conversations);
        var store = Store();
        var summary = Summary + new string('x', 7000) + "THE-END";
        await store.SaveSubAgentSessionAsync(Info(summary));
        var manager = Manager(Store());
        var tool = new SubAgentManageTool(manager, Parent);
        var args = Args("result");
        await tool.PrepareArgumentsAsync(args);
        var first = Read(await tool.ExecuteAsync("first", args));
        first.GetProperty("summaryTruncated").GetBoolean().ShouldBeTrue();
        first.GetProperty("originalLength").GetInt32().ShouldBe(summary.Length);
        first.GetProperty("content").GetString().ShouldBe(summary[..2048]);
        first.GetProperty("nextOffset").GetInt32().ShouldBe(2048);
        Read(await tool.ExecuteAsync("again", args)).GetRawText().ShouldBe(first.GetRawText());
        var all = "";
        for (var offset = 0; offset < summary.Length; offset += 2048)
            all += Read(await tool.ExecuteAsync("page", Args("result", offset))).GetProperty("content").GetString();
        all.ShouldBe(summary);
    }

    [Fact]
    public async Task History_ProjectsOnlyBoundedRedactedToolEvidenceAndNoReasoning()
    {
        var store = new InMemorySessionStore();
        await store.SaveSubAgentSessionAsync(Info("done"));
        var child = await store.GetOrCreateAsync(Child, AgentId.From("child-agent"));
        child.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "PRIVATE-SYSTEM" });
        child.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "answer", ThinkingContent = "PRIVATE-REASONING" });
        child.AddEntry(new SessionEntry { Role = MessageRole.Tool, Kind = MessageKind.ToolResult,
            ToolCallId = "tool-1", ToolName = "read", ToolArgs = "token=supersecret", Content = "ghp_1234567890123456789012345 " + new string('x', 5000), ThinkingContent = "PRIVATE-REASONING" });
        await store.SaveAsync(child);
        var tool = new SubAgentManageTool(Manager(store), Parent);
        var args = Args("history");
        args["limit"] = 100000;
        var result = Read(await tool.ExecuteAsync("history", args));
        var text = result.GetRawText();
        text.ShouldNotContain("PRIVATE");
        text.ShouldNotContain("supersecret");
        text.ShouldNotContain("ghp_");
        var entries = result.GetProperty("entries");
        entries.GetArrayLength().ShouldBe(1);
        entries[0].GetProperty("content").GetString().ShouldNotBeNull().Length.ShouldBeLessThanOrEqualTo(2048);
        entries[0].GetProperty("contentTruncated").GetBoolean().ShouldBeTrue();
        entries[0].GetProperty("toolCallId").GetString().ShouldBe("tool-1");
    }

    [Theory]
    [InlineData("sibling-session")]
    [InlineData("unrelated-session")]
    [InlineData("child-session")]
    [InlineData("parent-session-rollover")]
    public async Task Inspection_RejectsNonOriginalParent(string requester)
    {
        var store = new InMemorySessionStore();
        await store.SaveSubAgentSessionAsync(Info("done"));
        var tool = new SubAgentManageTool(Manager(store), SessionId.From(requester));
        foreach (var action in new[] { "result", "history" })
            await Should.ThrowAsync<UnauthorizedAccessException>(() => tool.ExecuteAsync("denied", Args(action)));
    }

    [Fact]
    public async Task Inspection_RejectsInvalidPagingAndUnsupportedMessage()
    {
        var tool = new SubAgentManageTool(new Mock<ISubAgentManager>(MockBehavior.Strict).Object, Parent);
        await Should.ThrowAsync<ArgumentException>(() => tool.PrepareArgumentsAsync(Args("result", -1)));
        var error = await Should.ThrowAsync<ArgumentException>(() => tool.PrepareArgumentsAsync(Args("message")));
        error.Message.ShouldContain("spawn_subagent");
    }

    [Fact]
    public void Projection_LongSummary_ExplicitlyReportsTruncation()
    {
        var detail = SubAgentRunDetail.FromLive(Info(new string('x', 4000)));
        var json = JsonSerializer.SerializeToElement(detail, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        json.GetProperty("summaryTruncated").GetBoolean().ShouldBeTrue();
        json.GetProperty("originalLength").GetInt32().ShouldBe(4000);
        json.GetProperty("resultReference").GetString().ShouldBe("run-1");
    }

    [Fact]
    public async Task Result_AfterConsumption_StillRepeatableAndPreservesFullSummary()
    {
        var fs = new MockFileSystem();
        var conversations = new InMemoryConversationStore();
        var root = Path.Combine(Path.GetTempPath(), "consumed-inspection-tests");
        FileSessionStore Store() => new(root, NullLogger<FileSessionStore>.Instance, fs, conversations);
        var store = Store();
        var parent = await store.GetOrCreateAsync(Parent, AgentId.From("parent-agent"));
        await store.SaveAsync(parent);
        var summary = new string('x', 6000);
        await store.SaveSubAgentSessionAsync(Info(summary));
        var tool = new SubAgentManageTool(Manager(Store()), Parent);
        await tool.ExecuteAsync("consume", Args("wait"));
        var before = (await Store().GetAsync(Parent)).ShouldNotBeNull().MessageCount;
        for (var i = 0; i < 2; i++)
        {
            var result = Read(await tool.ExecuteAsync("inspect-" + i, Args("result", 4096)));
            result.GetProperty("originalLength").GetInt32().ShouldBe(6000);
            result.GetProperty("content").GetString().ShouldBe(summary[4096..]);
        }
        (await Store().GetAsync(Parent)).ShouldNotBeNull().MessageCount.ShouldBe(before);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("file")]
    [InlineData("sqlite")]
    public async Task Store_ColdReadRetainsFullSummaryAndRejectsRelationshipRebinding(string kind)
    {
        var fs = new MockFileSystem();
        var conversations = new InMemoryConversationStore();
        var root = Path.Combine(Path.GetTempPath(), "retained-inspection-" + Guid.NewGuid().ToString("N"));
        var memory = new InMemorySessionStore();
        BotNexus.Gateway.Abstractions.Sessions.ISessionStore Store() => kind switch
        {
            "memory" => memory,
            "file" => new FileSessionStore(root, NullLogger<FileSessionStore>.Instance, fs, conversations),
            _ => new SqliteSessionStore(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = root + ".db", Pooling = false }.ToString(), NullLogger<SqliteSessionStore>.Instance, conversations)
        };
        try
        {
            var summary = new string('z', 7000) + "final-tail";
            var info = Info("running") with { Status = SubAgentStatus.Running };
            await Store().SaveSubAgentSessionAsync(info);
            await Store().UpdateSubAgentSessionAsync(Info(summary));
            var tool = new SubAgentManageTool(Manager(Store()), Parent);
            Read(await tool.ExecuteAsync("read", Args("result", 6144))).GetProperty("content").GetString().ShouldBe(summary[6144..]);
            var attack = Info(summary) with { ParentSessionId = SessionId.From("attacker") };
            await Should.ThrowAsync<UnauthorizedAccessException>(() => Store().UpdateSubAgentSessionAsync(attack));
            var childAttack = Info(summary) with { ChildSessionId = SessionId.From("victim") };
            await Should.ThrowAsync<UnauthorizedAccessException>(() => Store().UpdateSubAgentSessionAsync(childAttack));
        }
        finally
        {
            if (kind == "sqlite")
                foreach (var suffix in new[] { ".db", ".db-wal", ".db-shm" }) File.Delete(root + suffix);
        }
    }

    [Fact]
    public async Task History_PaginatesAndRejectsReboundChild()
    {
        var store = new InMemorySessionStore();
        await store.SaveSubAgentSessionAsync(Info("done"));
        var child = await store.GetOrCreateAsync(Child, AgentId.From("child-agent"));
        for (var i = 0; i < 25; i++)
            child.AddEntry(new SessionEntry { Role = MessageRole.Tool, Kind = MessageKind.ToolResult, ToolName = "read", Content = "evidence-" + i });
        var tool = new SubAgentManageTool(Manager(store), Parent);
        var first = Read(await tool.ExecuteAsync("page1", Args("history")));
        first.GetProperty("entries").GetArrayLength().ShouldBe(20);
        first.GetProperty("nextOffset").GetInt32().ShouldBe(20);
        var last = Read(await tool.ExecuteAsync("page2", Args("history", 20)));
        last.GetProperty("entries").GetArrayLength().ShouldBe(5);
        last.GetProperty("nextOffset").ValueKind.ShouldBe(JsonValueKind.Null);
        child.ConversationId = ConversationId.From("rebound");
        // An admission with an exact conversation reference must never inspect a rebound child.
        await store.SaveSubAgentSessionAsync(Info("done") with { SubAgentId = "run-2", ChildConversationId = ConversationId.From("original") });
        var args = Args("history"); args["subAgentId"] = "run-2";
        await Should.ThrowAsync<UnauthorizedAccessException>(() => tool.ExecuteAsync("rebound", args));
    }

    [Fact]
    public async Task Inspection_MissingAdmissionFailsClosedEvenWhenLiveManagerKnowsRun()
    {
        var store = new InMemorySessionStore();
        var tool = new SubAgentManageTool(Manager(store), Parent);
        await Should.ThrowAsync<KeyNotFoundException>(() => tool.ExecuteAsync("missing", Args("result")));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => tool.ExecuteAsync("cancelled", Args("result"), cancelled.Token));
    }

    [Fact]
    public async Task ConversationMessage_ChildConversationRejectsWithoutDispatchOrRestart()
    {
        var conversations = new InMemoryConversationStore();
        var id = ConversationId.From("child-conversation");
        await conversations.CreateAsync(new Conversation { ConversationId = id,
            AgentId = AgentId.From("child-agent"), Kind = ConversationKind.AgentSubAgent });
        var orchestrator = new Mock<BotNexus.Gateway.Dispatching.IInboundMessageOrchestrator>(MockBehavior.Strict);
        var router = new Mock<BotNexus.Gateway.Abstractions.Conversations.IConversationRouter>(MockBehavior.Strict);
        var tool = new ConversationTool(conversations, AgentId.From("parent-agent"),
            accessLevel: ConversationAccessLevel.Own, sessionStore: new InMemorySessionStore(),
            messageOrchestrator: orchestrator.Object, conversationRouter: router.Object);
        var error = await Should.ThrowAsync<NotSupportedException>(() => tool.ExecuteAsync("message", new Dictionary<string, object?>
            { ["action"] = "message", ["conversationId"] = id.Value, ["message"] = "continue" }));
        error.Message.ShouldContain("manage_subagent");
        error.Message.ShouldContain("spawn_subagent");
        (await conversations.GetAsync(id)).ShouldNotBeNull().ActiveSessionId.ShouldBeNull();
        orchestrator.VerifyNoOtherCalls();
        router.VerifyNoOtherCalls();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => tool.ExecuteAsync("read-denied", new Dictionary<string, object?>
            { ["action"] = "get", ["conversationId"] = id.Value }));
    }

    [Fact]
    public void PromptGuidance_UsesCurrentAwaitBackgroundWaitAndRetainedInspection()
    {
        foreach (var mode in new[] { PromptMode.Full, PromptMode.Minimal })
        {
            var prompt = SystemPromptBuilder.Build(new SystemPromptParams { WorkspaceDir = Path.GetTempPath(),
                PromptMode = mode, ToolNames = ["spawn_subagent", "manage_subagent", "list_subagents"] });
            prompt.ShouldContain("background=true");
            prompt.ShouldContain("action=wait");
            prompt.ShouldContain("action=result/history");
            prompt.ShouldContain("does not push an automatic inbound turn");
            prompt.ShouldNotContain("Completion is push-based");
            prompt.ShouldNotContain("subagents(action=list|steer|kill)");
        }
    }

    [Fact]
    public async Task MemoryReaders_ReturnFreshBoundedBaseObjectsUnderRuntimeSerialization()
    {
        var store = new InMemorySessionStore();
        var summary = new string('x', 6000) + "PRIVATE-TAIL";
        await store.SaveSubAgentSessionAsync(Info(summary) with { SpawningToolCallId = "spawn" });
        var first = (await store.GetSubAgentSessionAsync("run-1")).ShouldNotBeNull();
        var again = (await store.GetSubAgentSessionAsync("run-1")).ShouldNotBeNull();
        ReferenceEquals(first, again).ShouldBeFalse();
        var found = (await store.FindSubAgentSpawnAsync(Parent, "spawn")).ShouldNotBeNull();
        var listed = await store.ListSubAgentSessionsAsync(Parent);
        var all = await store.ListAllSubAgentSessionsAsync();
        foreach (var detail in new[] { first, again, found }.Concat(listed).Concat(all))
        {
            var runtimeJson = JsonSerializer.Serialize((object)detail);
            runtimeJson.ShouldNotContain("FullResultSummary");
            runtimeJson.ShouldNotContain("PRIVATE-TAIL");
            detail.GetType().ShouldBe(typeof(SubAgentRunDetail));
            detail.ResultSummary.ShouldNotBeNull().Length.ShouldBe(2048);
            detail.OriginalLength.ShouldBe(summary.Length);
            detail.SummaryTruncated.ShouldBeTrue();
        }
        JsonSerializer.Serialize(listed.Cast<object>().ToArray()).ShouldNotContain("PRIVATE-TAIL");
        JsonSerializer.Serialize((object)all).ShouldNotContain("FullResultSummary");
        (await store.GetSubAgentResultSummaryAsync("run-1")).ShouldBe(summary);
    }

    [Fact]
    public async Task MemoryWrites_ApplyConfiguredRedactorToAdmissionAndUpdates()
    {
        var redactor = new Mock<BotNexus.Gateway.Abstractions.Security.ISecretRedactor>();
        redactor.Setup(r => r.Redact(It.IsAny<string>())).Returns<string>(text => text.Replace("operator-private-marker", "[REDACTED]", StringComparison.Ordinal));
        var store = new InMemorySessionStore(redactor.Object);
        await store.SaveSubAgentSessionAsync(Info("operator-private-marker admitted"));
        (await store.GetSubAgentResultSummaryAsync("run-1")).ShouldBe("[REDACTED] admitted");
        (await store.GetSubAgentSessionAsync("run-1")).ShouldNotBeNull().ResultSummary.ShouldBe("[REDACTED] admitted");
        await store.UpdateSubAgentSessionAsync(Info("operator-private-marker completed"));
        var json = Read(await new SubAgentManageTool(Manager(store), Parent).ExecuteAsync("read", Args("result")));
        json.GetProperty("content").GetString().ShouldBe("[REDACTED] completed");
        JsonSerializer.Serialize((object)await store.ListAllSubAgentSessionsAsync()).ShouldNotContain("operator-private-marker");
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("file")]
    [InlineData("sqlite")]
    public async Task Result_RejectsNonTerminalThenReturnsRepeatableTerminalPages(string kind)
    {
        var fs = new MockFileSystem();
        var conversations = new InMemoryConversationStore();
        var root = Path.Combine(Path.GetTempPath(), "terminal-inspection-" + Guid.NewGuid().ToString("N"));
        var memory = new InMemorySessionStore();
        BotNexus.Gateway.Abstractions.Sessions.ISessionStore Store() => kind switch
        {
            "memory" => memory,
            "file" => new FileSessionStore(root, NullLogger<FileSessionStore>.Instance, fs, conversations),
            _ => new SqliteSessionStore(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = root + ".db", Pooling = false }.ToString(), NullLogger<SqliteSessionStore>.Instance, conversations)
        };
        try
        {
            await Store().SaveSubAgentSessionAsync(Info("INCOMPLETE") with { Status = SubAgentStatus.Running });
            var tool = new SubAgentManageTool(Manager(Store()), Parent);
            var error = await Should.ThrowAsync<InvalidOperationException>(() => tool.ExecuteAsync("running", Args("result")));
            error.Message.ShouldContain("terminal");
            error.Message.ShouldContain("wait");
            var summary = new string('z', 6000) + "FINAL";
            await Store().UpdateSubAgentSessionAsync(Info(summary));
            var first = Read(await tool.ExecuteAsync("first", Args("result", 4096)));
            first.GetProperty("content").GetString().ShouldBe(summary[4096..]);
            Read(await new SubAgentManageTool(Manager(Store()), Parent).ExecuteAsync("repeat", Args("result", 4096))).GetRawText().ShouldBe(first.GetRawText());
            var coldInfo = (await Manager(Store()).GetAsync("run-1")).ShouldNotBeNull();
            var status = SubAgentRunDetail.FromLive(coldInfo);
            status.OriginalLength.ShouldBe(summary.Length);
            status.SummaryTruncated.ShouldBeTrue();
            var listed = (await Manager(Store()).ListAsync(Parent)).Single();
            var projection = SubAgentRunDetail.FromLive(listed);
            projection.OriginalLength.ShouldBe(summary.Length);
            projection.SummaryTruncated.ShouldBeTrue();
        }
        finally
        {
            if (kind == "sqlite")
                foreach (var suffix in new[] { ".db", ".db-wal", ".db-shm", ".db.botnexus-fk-validation", ".db.botnexus-fk-validation.lock" }) File.Delete(root + suffix);
        }
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("file")]
    [InlineData("sqlite")]
    public async Task History_UpdatedConfiguredRules_RedactEveryLegacyExportBeforeBounding(string kind)
    {
        const string marker = "operator-private-marker";
        const string builtin = "ghp_1234567890123456789012345";
        var fs = new MockFileSystem();
        var conversations = new InMemoryConversationStore();
        var root = Path.Combine(Path.GetTempPath(), "legacy-inspection-" + Guid.NewGuid().ToString("N"));
        var memory = new InMemorySessionStore();
        BotNexus.Gateway.Abstractions.Sessions.ISessionStore Store(BotNexus.Gateway.Abstractions.Security.ISecretRedactor? redactor = null) => kind switch
        {
            "memory" => memory,
            "file" => new FileSessionStore(root, NullLogger<FileSessionStore>.Instance, fs, conversations, redactor),
            _ => new SqliteSessionStore(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = root + ".db", Pooling = false }.ToString(), NullLogger<SqliteSessionStore>.Instance, conversations, redactor: redactor)
        };
        try
        {
            // Persist under the OLD rules, then recreate store/manager with new operator rules.
            // In-memory deliberately retains the old session redactor, just like a cached legacy row.
            var original = Store();
            await original.SaveSubAgentSessionAsync(Info(marker + " final"));
            var child = await original.GetOrCreateAsync(Child, AgentId.From("child-agent"));
            foreach (var prefix in new[] { "", new string('x', 2040) })
            {
                var raw = prefix + marker + " " + builtin;
                child.AddEntry(new SessionEntry { Role = MessageRole.Tool, Kind = MessageKind.ToolResult,
                    Content = raw, ToolArgs = raw, ToolName = raw, ToolCallId = raw });
            }
            await original.SaveAsync(child);
            (await Store().GetAsync(Child)).ShouldNotBeNull().GetHistorySnapshot()[0].Content.ShouldContain(marker);
            var redactor = new BotNexus.Gateway.Security.SecretRedactor(options: new SecretRedactionOptions([marker]));
            var tool = new SubAgentManageTool(Manager(Store(redactor), redactor), Parent);
            var history = Read(await tool.ExecuteAsync("legacy-history", Args("history")));
            var entries = history.GetProperty("entries");
            entries.GetArrayLength().ShouldBe(2);
            foreach (var field in new[] { "content", "arguments", "toolName", "toolCallId" })
            {
                entries[0].GetProperty(field).GetString().ShouldBe("[REDACTED] [redacted-secret]");
                entries[1].GetProperty(field).GetString().ShouldBe(new string('x', 2040) + "[REDACTE");
            }
            history.GetRawText().ShouldNotContain(marker);
            entries[1].GetProperty("originalLength").GetInt32().ShouldBe(2068);
            entries[1].GetProperty("contentTruncated").GetBoolean().ShouldBeTrue();
            Read(await tool.ExecuteAsync("legacy-result", Args("result"))).GetProperty("content").GetString().ShouldBe("[REDACTED] final");
        }
        finally
        {
            if (kind == "sqlite")
                foreach (var suffix in new[] { ".db", ".db-wal", ".db-shm", ".db.botnexus-fk-validation", ".db.botnexus-fk-validation.lock" }) File.Delete(root + suffix);
        }
    }

    [Theory]
    [InlineData("memory", "status", false)]
    [InlineData("memory", "wait", false)]
    [InlineData("file", "status", false)]
    [InlineData("file", "wait", false)]
    [InlineData("sqlite", "status", false)]
    [InlineData("sqlite", "wait", false)]
    [InlineData("memory", "wait", true)]
    [InlineData("file", "wait", true)]
    [InlineData("sqlite", "wait", true)]
    public async Task Result_PreEnvelopeLegacyConsumption_NeverPromotesBoundedSummaryToFull(string kind, string action, bool knownLength)
    {
        var fs = new MockFileSystem();
        var conversations = new InMemoryConversationStore();
        var root = Path.Combine(Path.GetTempPath(), "pre-envelope-inspection-" + Guid.NewGuid().ToString("N"));
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = root + ".db", Pooling = false }.ToString();
        var memory = new InMemorySessionStore();
        BotNexus.Gateway.Abstractions.Sessions.ISessionStore Store() => kind switch
        {
            "memory" => memory,
            "file" => new FileSessionStore(root, NullLogger<FileSessionStore>.Instance, fs, conversations),
            _ => new SqliteSessionStore(connectionString, NullLogger<SqliteSessionStore>.Instance, conversations)
        };
        try
        {
            var store = Store();
            var parent = await store.GetOrCreateAsync(Parent, AgentId.From("parent-agent"));
            await store.SaveAsync(parent);
            await store.SaveSubAgentSessionAsync(Info("placeholder"));
            var summary = new string('l', 2048);
            // Write the old bounded record itself, not today's full-copy envelope with an old redactor.
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var node = JsonSerializer.SerializeToNode(SubAgentRunDetail.FromLive(Info(summary)), jsonOptions).ShouldNotBeNull().AsObject();
            node.ContainsKey("fullResultSummary").ShouldBeFalse();
            if (knownLength)
            {
                node["originalLength"] = 7000;
                node["summaryTruncated"] = true;
            }
            else
            {
                node.Remove("originalLength");
                node.Remove("summaryTruncated");
                node.Remove("resultReference");
            }
            var legacyJson = node.ToJsonString();
            if (kind == "memory")
            {
                // Focused fixture injection: no product API for constructing pre-envelope records.
                var field = typeof(InMemorySessionStore).GetField("_subAgents", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).ShouldNotBeNull();
                var runs = field.GetValue(memory) as System.Collections.IDictionary
                    ?? throw new InvalidOperationException("Expected the retained-run dictionary fixture.");
                var retainedType = field.FieldType.GetGenericArguments()[1];
                runs["run-1"] = JsonSerializer.Deserialize(legacyJson, retainedType, jsonOptions).ShouldNotBeNull();
            }
            else if (kind == "file")
                fs.File.WriteAllText(fs.AllFiles.Single(p => Path.GetFileName(p) == "run-1.json"), legacyJson);
            else
            {
                await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE sub_agent_sessions SET detail_json=$json WHERE id='run-1'";
                command.Parameters.AddWithValue("$json", legacyJson);
                (await command.ExecuteNonQueryAsync()).ShouldBe(1);
            }
            var original = (await Store().GetSubAgentSessionAsync("run-1")).ShouldNotBeNull();
            original.OriginalLength.ShouldBe(knownLength ? 7000 : null);
            original.SummaryTruncated.ShouldBe(knownLength);
            var tool = new SubAgentManageTool(Manager(Store()), Parent);
            var before = Read(await tool.ExecuteAsync("before", Args("result")));
            before.GetProperty("fullResultAvailable").GetBoolean().ShouldBeFalse();
            before.GetProperty("warning").GetString().ShouldNotBeNull().ShouldContain("Legacy record");
            before.GetProperty("content").GetString().ShouldBe(summary);
            await tool.ExecuteAsync("consume-legacy", Args(action));
            var coldTool = new SubAgentManageTool(Manager(Store()), Parent);
            var after = Read(await coldTool.ExecuteAsync("after", Args("result")));
            after.GetProperty("fullResultAvailable").GetBoolean().ShouldBeFalse();
            after.GetProperty("warning").GetString().ShouldBe(before.GetProperty("warning").GetString());
            after.GetProperty("content").GetString().ShouldBe(summary);
            after.GetProperty("originalLength").GetRawText().ShouldBe(knownLength ? "7000" : "null");
            after.GetProperty("summaryTruncated").GetBoolean().ShouldBe(knownLength);
            (await Store().GetSubAgentResultSummaryAsync("run-1")).ShouldBeNull();
            var retained = (await Store().GetSubAgentSessionAsync("run-1")).ShouldNotBeNull();
            retained.OriginalLength.ShouldBe(original.OriginalLength);
            retained.SummaryTruncated.ShouldBe(original.SummaryTruncated);
            var projection = SubAgentRunDetail.FromLive((await Manager(Store()).GetAsync("run-1")).ShouldNotBeNull());
            projection.OriginalLength.ShouldBe(original.OriginalLength);
            projection.SummaryTruncated.ShouldBe(original.SummaryTruncated);
            Read(await coldTool.ExecuteAsync("repeat", Args("result"))).GetRawText().ShouldBe(after.GetRawText());
        }
        finally
        {
            if (kind == "sqlite")
                foreach (var suffix in new[] { ".db", ".db-wal", ".db-shm", ".db.botnexus-fk-validation", ".db.botnexus-fk-validation.lock" }) File.Delete(root + suffix);
        }
    }

    private static Dictionary<string, object?> Args(string action, int offset = 0) => new()
        { ["subAgentId"] = "run-1", ["action"] = action, ["offset"] = offset };
    private static JsonElement Read(AgentToolResult result) => JsonDocument.Parse(result.Content.Single().Value ?? "{}").RootElement.Clone();
    private static SubAgentInfo Info(string summary) => new()
    {
        SubAgentId = "run-1", ParentSessionId = Parent, ChildSessionId = Child,
        ParentAgentId = "parent-agent", ChildAgentId = "child-agent", Task = "task",
        Status = SubAgentStatus.Completed, ResultSummary = summary,
        StartedAt = DateTimeOffset.UnixEpoch, CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(1)
    };
    private static DefaultSubAgentManager Manager(BotNexus.Gateway.Abstractions.Sessions.ISessionStore store,
        BotNexus.Gateway.Abstractions.Security.ISecretRedactor? redactor = null)
    {
        var options = new Mock<IOptionsMonitor<GatewayOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(new GatewayOptions());
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(store);
        if (redactor is not null) services.AddSingleton(redactor);
        using var provider = services.BuildServiceProvider();
        return Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<DefaultSubAgentManager>(provider,
            Mock.Of<IAgentSupervisor>(), Mock.Of<IAgentRegistry>(), Mock.Of<IActivityBroadcaster>(),
            Mock.Of<IChannelDispatcher>(), options.Object, NullLogger<DefaultSubAgentManager>.Instance);
    }
}
