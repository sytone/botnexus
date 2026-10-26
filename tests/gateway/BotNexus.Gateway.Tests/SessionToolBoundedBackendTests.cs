using System.Diagnostics;
using System.IO.Abstractions;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>Credential-free peer fixture: no host, gateway, live store, network or provider.
/// Search budgets: <=100 candidates, <=2000 entries, <=1Mi characters, <=2Mi managed UTF-16
/// content bytes. These are work counters, not timing-sensitive allocation estimates.</summary>
public sealed class SessionToolBoundedBackendTests
{
    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("file")]
    public async Task Search_UnrelatedCorpusDoesNotMaterializeTranscripts(string backend)
    {
        using var fixture = new Fixture(backend);
        var owner = await fixture.AddConversation("owner");
        var peer = await fixture.AddConversation("peer");
        // 128 unrelated sessions, each with 64KiB content; data must not enter the read path.
        for (var i = 0; i < 128; i++)
            await fixture.AddSession($"unrelated-{i:D3}", peer, "peer", new string('x', 65_536));
        await fixture.AddSession("own-b", owner, "owner", "no match");
        await fixture.AddSession("own-a", owner, "owner", "needle" + new string('z', 240), "NEEDLE two", "Needle three", "needle fourth");
        var store = fixture.CreateStore(); // Cold File/SQLite instance; seeded cache cannot mask reads.
        var measured = new Mock<ISessionStore>(MockBehavior.Strict);
        var summaries = 0;
        var scans = 0;
        var bytes = 0;
        measured.Setup(s => s.ListSummaryPageAsync(It.IsAny<SessionSummaryQuery>(), It.IsAny<CancellationToken>()))
            .Returns(async (SessionSummaryQuery query, CancellationToken ct) =>
            {
                summaries++;
                query.AgentId.ShouldBe("owner");
                query.Limit.ShouldBe(100);
                query.MetadataOnly.ShouldBeTrue();
                return await store.ListSummaryPageAsync(query, ct);
            });
        measured.Setup(s => s.ScanHistoryAsync(It.IsAny<SessionId>(), It.IsAny<SessionHistoryScan>(), It.IsAny<CancellationToken>()))
            .Returns(async (SessionId id, SessionHistoryScan scan, CancellationToken ct) =>
            {
                scans++;
                id.Value.ShouldStartWith("own-");
                var page = await store.ScanHistoryAsync(id, scan, ct);
                bytes += page.Entries.Sum(e => e.Content.Length * sizeof(char));
                return page;
            });
        var stopwatch = Stopwatch.StartNew();
        var result = await new SessionTool(measured.Object, AgentId.From("owner")).ExecuteAsync("test",
            new Dictionary<string, object?> { ["action"] = "search", ["query"] = "nEeDlE" });
        using var json = JsonDocument.Parse(result.Content[0].Value);
        var root = json.RootElement;
        var item = root.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("sessionId").GetString().ShouldBe("own-a");
        item.GetProperty("matchCount").GetInt32().ShouldBe(3);
        item.GetProperty("matches")[0].GetProperty("contentPreview").GetString()?.Length.ShouldBeLessThanOrEqualTo(203);
        root.GetProperty("complete").GetBoolean().ShouldBeTrue();
        root.GetProperty("scannedSessions").GetInt32().ShouldBe(2);
        summaries.ShouldBe(1);
        scans.ShouldBe(2);
        bytes.ShouldBeLessThan(2048);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        Console.WriteLine($"METRIC backend={backend} unrelated=128x65536 summaryQueries={summaries} historyQueries={scans} materializedContentBytes={bytes} elapsedMs={stopwatch.ElapsedMilliseconds}");
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("file")]
    public async Task Store_HugeEntryAndWrongOwnerAreBounded(string backend)
    {
        using var fixture = new Fixture(backend);
        var owner = await fixture.AddConversation("owner");
        await fixture.AddSession("huge", owner, "owner", new string('x', 2_000_000));
        var store = fixture.CreateStore();
        var summaries = await store.ListSummaryPageAsync(new(DateTimeOffset.MinValue, AgentId: "owner", Limit: 1, MetadataOnly: true));
        summaries.Items.ShouldHaveSingleItem().MessageCount.ShouldBe(1);
        var stopwatch = Stopwatch.StartNew();
        var scan = await store.ScanHistoryAsync(SessionId.From("huge"), new(AgentId.From("owner"), 10, 1024));
        scan.Complete.ShouldBeFalse();
        scan.Entries.ShouldBeEmpty();
        scan.MaterializedCharacters.ShouldBeLessThanOrEqualTo(1024);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => store.ScanHistoryAsync(
            SessionId.From("huge"), new(AgentId.From("peer"), 10, 1024)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => store.ScanHistoryAsync(
            SessionId.From("huge"), new(AgentId.From("owner"), 10, 1024), cts.Token));
        Console.WriteLine($"METRIC backend={backend} hugeEntry=2000000 scanCharacters={scan.MaterializedCharacters} elapsedMs={stopwatch.ElapsedMilliseconds}");
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("file")]
    public async Task Search_AllAndAllowlistAccessAndEntryBudgets(string backend)
    {
        using var fixture = new Fixture(backend);
        var owner = await fixture.AddConversation("owner");
        var peer = await fixture.AddConversation("peer");
        await fixture.AddSession("own", owner, "owner", "needle");
        await fixture.AddSession("peer", peer, "peer", "needle one", "needle two", "needle three");
        var store = fixture.CreateStore();
        var args = new Dictionary<string, object?> { ["action"] = "search", ["query"] = "needle" };
        using var all = JsonDocument.Parse((await new SessionTool(store, AgentId.From("owner"), SessionAccessLevel.All)
            .ExecuteAsync("test", args)).Content[0].Value);
        all.RootElement.GetProperty("items").GetArrayLength().ShouldBe(2);
        all.RootElement.GetProperty("complete").GetBoolean().ShouldBeTrue();
        args["agentId"] = "peer";
        using var allowed = JsonDocument.Parse((await new SessionTool(store, AgentId.From("owner"), SessionAccessLevel.Allowlist, ["peer"])
            .ExecuteAsync("test", args)).Content[0].Value);
        allowed.RootElement.GetProperty("items").EnumerateArray().ShouldHaveSingleItem()
            .GetProperty("sessionId").GetString().ShouldBe("peer");
        var prefix = await store.ScanHistoryAsync(SessionId.From("peer"), new(AgentId.From("peer"), 2, 1024));
        prefix.Entries.Count.ShouldBe(2);
        prefix.Complete.ShouldBeFalse();
        prefix.MaterializedCharacters.ShouldBeLessThanOrEqualTo(1024);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => store.ScanHistoryAsync(
            SessionId.From("peer"), new(AgentId.From("peer"), int.MaxValue, int.MaxValue)));
    }

    [Fact]
    public async Task FileList_NeverOpensHistoryAndLegacyCountIsExplicitlyUnknown()
    {
        using var fixture = new Fixture("file");
        var owner = await fixture.AddConversation("owner");
        await fixture.AddSession("own", owner, "owner", "needle");
        var metaPath = Path.Combine(fixture.DirectoryPath, "own.meta.json");
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(metaPath));
        metadata.ShouldNotBeNull();
        metadata.AsObject().Remove("messageCount");
        await File.WriteAllTextAsync(metaPath, metadata.ToJsonString());
        // A metadata list that opens even its selected transcript fails deterministically.
        await using var exclusive = new FileStream(Path.Combine(fixture.DirectoryPath, "own.jsonl"),
            FileMode.Open, FileAccess.Read, FileShare.None);
        var store = fixture.CreateStore();
        var page = await store.ListSummaryPageAsync(new(DateTimeOffset.MinValue, AgentId: "owner", Limit: 1, MetadataOnly: true));
        page.Items.ShouldHaveSingleItem().MessageCount.ShouldBe(-1);
    }

    [Fact]
    public async Task Search_CandidatePagingAndManagedScanCancellationAreExplicit()
    {
        var store = new InMemorySessionStore();
        for (var i = 0; i < 102; i++)
            await store.SaveAsync(new GatewaySession { SessionId = SessionId.From($"s{i:D3}"), AgentId = AgentId.From("owner"), UpdatedAt = DateTimeOffset.UnixEpoch });
        var tool = new SessionTool(store, AgentId.From("owner"));
        var args = new Dictionary<string, object?> { ["action"] = "search", ["query"] = "needle" };
        using var first = JsonDocument.Parse((await tool.ExecuteAsync("test", args)).Content[0].Value);
        first.RootElement.GetProperty("complete").GetBoolean().ShouldBeFalse();
        first.RootElement.GetProperty("nextOffset").GetInt32().ShouldBe(100);
        args["offset"] = 100;
        using var second = JsonDocument.Parse((await tool.ExecuteAsync("test", args)).Content[0].Value);
        second.RootElement.GetProperty("complete").GetBoolean().ShouldBeTrue();
        second.RootElement.GetProperty("scannedSessions").GetInt32().ShouldBe(2);

        using var cts = new CancellationTokenSource();
        var cancelStore = new Mock<ISessionStore>(MockBehavior.Strict);
        cancelStore.Setup(s => s.ListSummaryPageAsync(It.IsAny<SessionSummaryQuery>(), cts.Token))
            .ReturnsAsync(await store.ListSummaryPageAsync(new(DateTimeOffset.MinValue, AgentId: "owner", Limit: 1)));
        cancelStore.Setup(s => s.ScanHistoryAsync(It.IsAny<SessionId>(), It.IsAny<SessionHistoryScan>(), cts.Token))
            .Returns(() => { cts.Cancel(); return Task.FromResult(new SessionHistoryScanPage(
                [new SessionEntry { Role = MessageRole.User, Content = "needle" }], true, 6)); });
        await Should.ThrowAsync<OperationCanceledException>(() => new SessionTool(cancelStore.Object, AgentId.From("owner"))
            .ExecuteAsync("test", args, cts.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteSummary_ProjectsOwnershipWithoutHydratingConversation(bool filtered)
    {
        var path = Path.Combine(Path.GetTempPath(), "bounded-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(path, "conversations.db"), Pooling = false }.ToString();
            var conversations = new SqliteConversationStore(cs, NullLogger<SqliteConversationStore>.Instance);
            var id = ConversationId.Create();
            await conversations.CreateAsync(new Conversation { ConversationId = id, AgentId = AgentId.From("owner") });
            var sessionsCs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(path, "sessions.db"), Pooling = false }.ToString();
            var writer = new SqliteSessionStore(sessionsCs, NullLogger<SqliteSessionStore>.Instance, conversations);
            var session = new GatewaySession { SessionId = SessionId.From("own"), AgentId = AgentId.From("owner"), ConversationId = id };
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "needle" });
            await writer.SaveAsync(session);
            await using (var db = new SqliteConnection(cs))
            {
                await db.OpenAsync();
                await using var command = db.CreateCommand();
                // Valid ownership remains readable even when unrelated aggregate JSON is corrupt.
                command.CommandText = "UPDATE conversations SET metadata='not-json', instructions=printf('%.*c', 8000000, 'x')";
                await command.ExecuteNonQueryAsync();
            }
            var coldConversations = new SqliteConversationStore(cs, NullLogger<SqliteConversationStore>.Instance);
            var store = new SqliteSessionStore(sessionsCs, NullLogger<SqliteSessionStore>.Instance, coldConversations);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var pending = store.ListSummaryPageAsync(new(DateTimeOffset.MinValue, AgentId: filtered ? "owner" : null, Limit: 1, MetadataOnly: true));
            pending.IsCompletedSuccessfully.ShouldBeTrue("SQLite projection must complete on the measuring thread");
            var page = await pending;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            page.Items.ShouldHaveSingleItem().AgentId.ShouldBe("owner");
            Console.WriteLine($"METRIC sqliteOwnership filtered={filtered} instructionsChars=8000000 allocatedBytes={allocated}");
            allocated.ShouldBeLessThan(2_000_000);
            var scan = await store.ScanHistoryAsync(SessionId.From("own"), new(AgentId.From("owner"), 10, 1024));
            scan.Entries.ShouldHaveSingleItem().Content.ShouldBe("needle");
            // Both caches are now warm. A canonical SQL deletion must still revoke access.
            await using (var db = new SqliteConnection(cs))
            {
                await db.OpenAsync();
                await using var command = db.CreateCommand();
                command.CommandText = "DELETE FROM conversations";
                await command.ExecuteNonQueryAsync();
            }
            await Should.ThrowAsync<UnauthorizedAccessException>(() => store.ScanHistoryAsync(
                SessionId.From("own"), new(AgentId.From("owner"), 10, 1024)));
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public async Task SqliteScan_WarmSummaryThenCanonicalDeleteRejectsBeforeContent()
    {
        using var fixture = new Fixture("sqlite");
        var owner = await fixture.AddConversation("owner");
        await fixture.AddSession("own", owner, "owner", "needle");
        var store = fixture.CreateStore();
        (await store.ListSummaryPageAsync(new(DateTimeOffset.MinValue, AgentId: "owner", Limit: 1, MetadataOnly: true))).Items.ShouldHaveSingleItem();
        // Remove the canonical owner while retaining the session and both warm caches.
        var field = typeof(InMemoryConversationStore).GetField("_conversations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.ShouldNotBeNull();
        var rows = field.GetValue(fixture.Conversations).ShouldBeOfType<System.Collections.Concurrent.ConcurrentDictionary<string, Conversation>>();
        rows.TryRemove(owner.Value, out _).ShouldBeTrue();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => store.ScanHistoryAsync(SessionId.From("own"), new(AgentId.From("owner"), 10, 1024)));
    }

    [Fact]
    public async Task FileSummary_HugeIgnoredMetadataHasBoundedParserAllocation()
    {
        using var fixture = new Fixture("file");
        var owner = await fixture.AddConversation("owner");
        await fixture.AddSession("own", owner, "owner", "needle");
        var path = Path.Combine(fixture.DirectoryPath, "own.meta.json");
        var original = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, "{\"ignored\":\"" + new string('x', 8_000_000) + "\"," + original[1..]);
        // Snapshot input outside measurement and return synchronous, non-copying streams.
        // All real store/parser code still runs; only disk scheduling is removed so parallel
        // tests cannot contaminate this thread-local allocation delta.
        var fileSystem = await SynchronousReadFileSystem(fixture.DirectoryPath);
        var store = new FileSessionStore(fixture.DirectoryPath, NullLogger<FileSessionStore>.Instance, fileSystem, fixture.Conversations);
        // Warm serializer metadata before measuring the projection parser itself.
        await store.ListSummaryPageAsync(new(DateTimeOffset.MinValue, AgentId: "owner", Limit: 1, MetadataOnly: true));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var pending = store.ListSummaryPageAsync(new(DateTimeOffset.MinValue, AgentId: "owner", Limit: 1, MetadataOnly: true));
        pending.IsCompletedSuccessfully.ShouldBeTrue("Memory-backed file reads must complete on the measuring thread");
        var page = await pending;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        page.Items.ShouldHaveSingleItem().AgentId.ShouldBe("owner");
        Console.WriteLine($"METRIC fileIgnoredMetadataBytes=8000000 allocatedBytes={allocated}");
        allocated.ShouldBeLessThan(2_000_000);
    }

    [Fact]
    public async Task MemoryScan_LargeHistoryMaterializesOnlyBoundedPrefix()
    {
        var store = new InMemorySessionStore();
        var session = new GatewaySession { SessionId = SessionId.From("large"), AgentId = AgentId.From("owner") };
        var entry = new SessionEntry { Role = MessageRole.User, Content = "needle" };
        session.AddEntries(Enumerable.Repeat(entry, 300_000));
        await store.SaveAsync(session);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var page = await store.ScanHistoryAsync(session.SessionId, new(session.AgentId, 10, 1024));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        page.Entries.Count.ShouldBe(10);
        page.Complete.ShouldBeFalse();
        allocated.ShouldBeLessThan(100_000);
        Console.WriteLine($"METRIC memoryHistoryEntries=300000 returnedEntries=10 allocatedBytes={allocated}");
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("file")]
    public async Task ConversationOwnership_ProjectsOnlyIdsAndOwnersAcrossBackends(string backend)
    {
        var path = Path.Combine(Path.GetTempPath(), "ownership-projection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            IConversationStore store = backend switch
            {
                "memory" => new InMemoryConversationStore(),
                "file" => new FileConversationStore(path, NullLogger<FileConversationStore>.Instance, new FileSystem()),
                "sqlite" => new SqliteConversationStore(new SqliteConnectionStringBuilder { DataSource = Path.Combine(path, "conversations.db"), Pooling = false }.ToString(), NullLogger<SqliteConversationStore>.Instance),
                _ => throw new ArgumentException("Unknown backend")
            };
            var owner = AgentId.From("owner");
            var id = ConversationId.Create();
            await store.CreateAsync(new Conversation { ConversationId = id, AgentId = owner, Instructions = new string('x', 8_000_000) });
            await store.CreateAsync(new Conversation { ConversationId = ConversationId.Create(), AgentId = AgentId.From("peer") });
            if (backend == "file")
                store = new FileConversationStore(path, NullLogger<FileConversationStore>.Instance, await SynchronousReadFileSystem(path));
            // These backends complete synchronously with the deterministic file-stream seam.
            var before = GC.GetAllocatedBytesForCurrentThread();
            var pendingOwner = store.GetOwnershipAsync(id);
            pendingOwner.IsCompletedSuccessfully.ShouldBeTrue("Ownership projection must complete on the measuring thread");
            var ownership = await pendingOwner;
            var pendingList = store.ListOwnershipAsync(owner);
            pendingList.IsCompletedSuccessfully.ShouldBeTrue("Ownership list must complete on the measuring thread");
            var own = await pendingList;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            ownership.ShouldBe(new ConversationOwnership(id, owner));
            own.ShouldHaveSingleItem().ShouldBe(ownership);
            (await store.ListOwnershipAsync()).Count.ShouldBe(2);
            (await store.GetOwnershipAsync(ConversationId.Create())).ShouldBeNull();
            allocated.ShouldBeLessThan(2_000_000);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => store.GetOwnershipAsync(id, canceled.Token));
            await Should.ThrowAsync<OperationCanceledException>(() => store.ListOwnershipAsync(ct: canceled.Token));
            Console.WriteLine($"METRIC ownershipBackend={backend} instructionsChars=8000000 allocatedBytes={allocated}");
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public async Task JsonProjection_EscapesNestedIgnoredValuesAndOversizedSelectedFields()
    {
        var id = ConversationId.Create();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var fields = new HashSet<string>(StringComparer.Ordinal) { "conversationId", "agentId" };
        var json = "{\"ignored\":[{\"x\":\"escaped\\\"quote}\\\\\"}],\"conversationId\":\"" + id.Value + "\",\"agentId\":\"owner\"}";
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        (await BoundedJsonProjection.ReadAsync<ConversationOwnership>(stream, options, fields)).ShouldBe(new ConversationOwnership(id, AgentId.From("owner")));
        await using var oversized = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"agentId\":\"" + new string('x', 5000) + "\"}"));
        await Should.ThrowAsync<JsonException>(() => BoundedJsonProjection.ReadAsync<ConversationOwnership>(oversized, options, fields));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemoryBoundedReads_UseFreshCanonicalOwnerAfterDeleteOrRebind(bool delete)
    {
        var conversations = new InMemoryConversationStore();
        var id = ConversationId.Create();
        var conversation = new Conversation { ConversationId = id, AgentId = AgentId.From("owner") };
        await conversations.CreateAsync(conversation);
        var store = new InMemorySessionStore(null, conversations);
        var session = new GatewaySession { SessionId = SessionId.From("own"), AgentId = conversation.AgentId, ConversationId = id };
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "needle" });
        await store.SaveAsync(session);
        var query = new SessionSummaryQuery(DateTimeOffset.MinValue, AgentId: "owner", MetadataOnly: true);
        (await store.ListSummaryPageAsync(query)).Items.ShouldHaveSingleItem();
        (await store.ScanHistoryAsync(session.SessionId, new(conversation.AgentId, 10, 1024))).Entries.ShouldHaveSingleItem();
        if (delete)
        {
            var field = typeof(InMemoryConversationStore).GetField("_conversations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field.ShouldNotBeNull();
            field.GetValue(conversations).ShouldBeOfType<System.Collections.Concurrent.ConcurrentDictionary<string, Conversation>>()
                .TryRemove(id.Value, out _).ShouldBeTrue();
        }
        else await conversations.SaveAsync(conversation with { AgentId = AgentId.From("peer") });
        (await store.ListSummaryPageAsync(query)).Items.ShouldBeEmpty();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => store.ScanHistoryAsync(session.SessionId, new(conversation.AgentId, 10, 1024)));
        if (!delete)
        {
            (await store.ListSummaryPageAsync(query with { AgentId = "peer" })).Items.ShouldHaveSingleItem().AgentId.ShouldBe("peer");
            (await store.ScanHistoryAsync(session.SessionId, new(AgentId.From("peer"), 10, 1024))).Entries.ShouldHaveSingleItem().Content.ShouldBe("needle");
        }
        session.AgentId.ShouldBe(conversation.AgentId);
    }

    [Theory]
    [InlineData("INVALID")]
    [InlineData("[1,,2]")]
    [InlineData("{\"x\"::1}")]
    [InlineData("01")]
    [InlineData("1e+")]
    [InlineData("tru")]
    [InlineData("\"bad\\q\"")]
    [InlineData("\"bad\\u00xz\"")]
    [InlineData("\"raw\nnewline\"")]
    [InlineData("{\"x\":[1}")]
    public async Task JsonProjection_InvalidIgnoredGrammarFailsClosed(string ignored)
    {
        var json = "{\"ignored\":" + ignored + ",\"agentId\":\"owner\"}";
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        await Should.ThrowAsync<JsonException>(() => BoundedJsonProjection.ReadAsync<Dictionary<string, string>>(stream,
            new JsonSerializerOptions(), new HashSet<string> { "agentId" }));
    }

    [Theory]
    [InlineData("{,\"agentId\":\"owner\"}")]
    [InlineData("{\"ignored\":1,,\"agentId\":\"owner\"}")]
    [InlineData("{\"agentId\"::\"owner\"}")]
    public async Task JsonProjection_InvalidObjectDelimitersFailClosed(string json)
    {
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        await Should.ThrowAsync<JsonException>(() => BoundedJsonProjection.ReadAsync<Dictionary<string, string>>(stream,
            new JsonSerializerOptions(), new HashSet<string> { "agentId" }));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("-12.34e+56")]
    [InlineData("[null,true,false,0,-1,2.3e-4,{\"x\":[]}]")]
    [InlineData("{\"x\":\"escaped\\u0041\\t\\n\"}")]
    public async Task JsonProjection_ValidIgnoredGrammarPreservesSelectedFields(string ignored)
    {
        var json = "{\"ignored\":" + ignored + ",\"agentId\":\"owner\"}";
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var result = await BoundedJsonProjection.ReadAsync<Dictionary<string, string>>(stream,
            new JsonSerializerOptions(), new HashSet<string> { "agentId" });
        result.ShouldNotBeNull()["agentId"].ShouldBe("owner");
    }

    [Theory]
    [InlineData("[1")]
    [InlineData("{\"x\":1")]
    [InlineData("[true false]")]
    [InlineData("{\"x\" 1}")]
    [InlineData("{\"x\":}")]
    [InlineData("+1")]
    [InlineData(".1")]
    [InlineData("1.")]
    [InlineData("-")]
    [InlineData("NaN")]
    [InlineData("truefalse")]
    [InlineData("1 2")]
    [InlineData("\"\\u123\"")]
    [InlineData("\"unterminated")]
    public Task JsonProjection_MoreInvalidIgnoredGrammarFailsClosed(string ignored)
        => JsonProjection_InvalidIgnoredGrammarFailsClosed(ignored);

    [Fact]
    public async Task JsonProjection_ChunkBoundariesHugeStringsAndGrammarLimits()
    {
        var fields = new HashSet<string> { "agentId" };
        var options = new JsonSerializerOptions();
        foreach (var padding in new[] { 4078, 4079, 4080, 4081, 4082, 4083, 4084, 4085 })
        {
            var json = "{\"ignored\":\"" + new string('x', padding) + "\\u0041\\\"quote\",\"agentId\":\"owner\"}";
            await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            (await BoundedJsonProjection.ReadAsync<Dictionary<string, string>>(stream, options, fields))
                .ShouldNotBeNull()["agentId"].ShouldBe("owner");
        }
        foreach (var json in new[]
        {
            "{\"ignored\":" + new string('[', 64) + "0" + new string(']', 64) + "}",
            "{\"ignored\":\"" + new string('x', 8000) + "\\q\",\"agentId\":\"owner\"}",
            "{} {}", "{}\u00a0", "", "{", "{\"ignored\":1"
        })
        {
            await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            await Should.ThrowAsync<JsonException>(() => BoundedJsonProjection.ReadAsync<Dictionary<string, string>>(stream, options, fields));
        }
    }

    private static async Task<IFileSystem> SynchronousReadFileSystem(string root)
    {
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
            contents.Add(path, await File.ReadAllBytesAsync(path));
        var real = new FileSystem();
        var streams = new Mock<IFileStreamFactory>(MockBehavior.Strict);
        streams.Setup(s => s.New(It.IsAny<string>(), FileMode.Open, FileAccess.Read, FileShare.Read))
            .Returns((string path, FileMode _, FileAccess _, FileShare _) =>
                new SynchronousFileStream(contents[path], path));
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        fileSystem.SetupGet(f => f.File).Returns(real.File);
        fileSystem.SetupGet(f => f.Directory).Returns(real.Directory);
        fileSystem.SetupGet(f => f.FileStream).Returns(streams.Object);
        return fileSystem.Object;
    }

    private sealed class SynchronousFileStream(byte[] contents, string path)
        : FileSystemStream(new MemoryStream(contents, writable: false), path, isAsync: false);

    private sealed class Fixture : IDisposable
    {
        private readonly string _backend;
        private readonly string _path = Path.Combine(Path.GetTempPath(), "bounded-session-test-" + Guid.NewGuid().ToString("N"));
        private readonly InMemoryConversationStore _conversations = new();
        private readonly InMemorySessionStore _memory;
        private readonly ISessionStore _writer;
        public string DirectoryPath => _path;
        public InMemoryConversationStore Conversations => _conversations;
        public Fixture(string backend)
        {
            _backend = backend;
            _memory = new InMemorySessionStore(null, _conversations);
            Directory.CreateDirectory(_path);
            _writer = CreateStore();
        }
        public ISessionStore CreateStore() => _backend switch
        {
            "memory" => _memory,
            "sqlite" => new SqliteSessionStore(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_path, "sessions.db"), Pooling = false }.ToString(), NullLogger<SqliteSessionStore>.Instance, _conversations),
            "file" => new FileSessionStore(_path, NullLogger<FileSessionStore>.Instance, new FileSystem(), _conversations),
            _ => throw new ArgumentException("Unknown backend")
        };
        public async Task<ConversationId> AddConversation(string agent)
        {
            var id = ConversationId.Create();
            await _conversations.CreateAsync(new Conversation { ConversationId = id, AgentId = AgentId.From(agent) });
            return id;
        }
        public async Task AddSession(string id, ConversationId conversation, string agent, params string[] content)
        {
            var session = new GatewaySession { SessionId = SessionId.From(id), AgentId = AgentId.From(agent), ConversationId = conversation };
            foreach (var text in content) session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = text });
            session.UpdatedAt = DateTimeOffset.UnixEpoch;
            await _writer.SaveAsync(session);
        }
        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
