using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using static BotNexus.Gateway.Tests.RunMeasurement4796Fixture;

namespace BotNexus.Gateway.Tests;

public sealed class SqliteRunMeasurement4796Tests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), nameof(SqliteRunMeasurement4796Tests), Guid.NewGuid().ToString("N"));
    private readonly InMemoryConversationStore _conversations = new();
    private string ConnectionString => $"Data Source={Path.Combine(_directory, "sessions.db")};Pooling=False";
    private static SessionId Session => SessionId.From("measured");
    public SqliteRunMeasurement4796Tests() => Directory.CreateDirectory(_directory);
    private SqliteSessionStore Store() => new(ConnectionString, NullLogger<SqliteSessionStore>.Instance, _conversations);
    private async Task<SqliteSessionStore> Initialized()
    {
        var store = Store();
        // GetOrCreateAsync only caches a new session; evidence requires its durable row.
        await store.SaveAsync(await store.GetOrCreateAsync(Session, AgentId.From("agent")));
        return store;
    }
    private static Task Add(object store, string id, int? count, string outcome = "Completed", IReadOnlyList<GuardObservation>? guards = null, bool terminal = true)
        => Record(store, Session, Evidence(store, id, count, outcome, guards, terminal));

    [Fact]
    public async Task PublicCapability_DomainEvidenceAndPage_ExposeExactPayloadFreeContract()
    {
        var store = await Initialized();
        EvidenceType(store).IsPublic.ShouldBeTrue();
        await Add(store, "public-contract", 0);
        var page = await Query(Store(), Session);
        var row = Rows(page).ShouldHaveSingleItem();
        RunId(row).ShouldBe("public-contract");
        Summary(page, 1, 0, 0, 0, 0, 0, 0);
        page.GetRawText().ShouldNotContain("private-payload-4796");
    }

    [Fact]
    public async Task Record_MissingOrCachedOnlySession_DoesNotCreateDurableRowsUntilExplicitSave()
    {
        var store = Store();
        var evidence = Evidence(store, "requires-save", 4, "Completed", terminal: true);
        await Record(store, Session, evidence);
        (await Store().GetAsync(Session)).ShouldBeNull();
        Rows(await Query(Store(), Session)).ShouldBeEmpty();

        var session = await store.GetOrCreateAsync(Session, AgentId.From("agent"));
        (await store.GetAsync(Session)).ShouldNotBeNull();
        await Record(store, Session, evidence);
        (await Store().GetAsync(Session)).ShouldBeNull("run evidence must not persist a cached-only session");
        Rows(await Query(store, Session)).ShouldBeEmpty();
        Rows(await Query(Store(), Session)).ShouldBeEmpty();

        await store.SaveAsync(session);
        (await Store().GetAsync(Session)).ShouldNotBeNull();
        await Record(store, Session, evidence);
        var page = await Query(Store(), Session);
        RunId(Rows(page).ShouldHaveSingleItem()).ShouldBe("requires-save");
        Summary(page, 1, 0, 0, 0, 4, 4, 4);
    }

    [Fact]
    public async Task Record_StartAndTerminalRetries_AreIdempotentAndTerminalCannotRegressOrConflict()
    {
        var store = await Initialized();
        var start = Evidence(store, "retry");
        await Record(store, Session, start);
        await Record(store, Session, start);
        var first = Rows(await Query(store, Session)).ShouldHaveSingleItem();
        first.GetProperty("CompletedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        first.GetProperty("CompletedResultCount").ValueKind.ShouldBe(JsonValueKind.Null);
        Summary(await Query(store, Session), 0, 0, 0, 1, null, null, null);
        var end = Evidence(store, "retry", 7, "Completed", terminal: true);
        await Record(store, Session, end);
        await Record(store, Session, end);
        await Record(store, Session, start);
        // A conflicting acknowledgement may be ignored or rejected, never replace the first terminal.
        try { await Add(store, "retry", 999, "Failed"); }
        catch (InvalidOperationException) { }
        var page = await Query(Store(), Session);
        var terminal = Rows(page).ShouldHaveSingleItem();
        Number(terminal, "Sequence").ShouldBe(Number(first, "Sequence"));
        terminal.GetProperty("Outcome").GetString().ShouldBe("Completed");
        Number(terminal, "CompletedResultCount").ShouldBe(7);
        Summary(page, 1, 0, 0, 0, 7, 7, 7);
    }

    [Fact]
    public async Task Query_NearestRankUsesOnlySelectedCompleteMeasuredRuns_SeparatesSemanticAndFuseStops()
    {
        var store = await Initialized();
        await Add(store, "one", 1);
        await Add(store, "semantic", 6, "Parked", [Guard()]);
        await Add(store, "fuse", 129, "Parked", [Guard(fuse: true, total: 129)]);
        await Add(store, "warning", 3, guards: [Guard("warning", total: 3)]);
        await Add(store, "recovered", 4, guards: [Guard("recovered", total: 3)]);
        await Add(store, "cancelled", 1000, "Cancelled");
        await Add(store, "failed", 1001, "Failed");
        await Add(store, "unmeasured", null);
        await Add(store, "in-flight", 1002, "Running", terminal: false);
        await Add(store, "unobserved-park", 1003, "Parked");
        var page = await Query(Store(), Session);
        Rows(page).Length.ShouldBe(10);
        // Sorted measured sample [1,3,4,6,129]; ceil(p*n), not interpolated percentiles.
        Summary(page, 5, 1, 1, 5, 4, 129, 129);
        var first = await Query(store, Session, 2);
        Rows(first).Length.ShouldBe(2);
        Summary(first, 2, 1, 0, 0, 1, 6, 6);
        first.GetProperty("HasMore").GetBoolean().ShouldBeTrue();
        Number(first, "NextCursor").ShouldBe(Number(Rows(first).Last(), "Sequence"));
    }

    [Fact]
    public async Task Query_NoMeasuredTerminalSample_ReportsNullPercentilesNotSyntheticZero()
    {
        var store = await Initialized();
        Summary(await Query(store, Session), 0, 0, 0, 0, null, null, null);
        await Add(store, "cancelled", null, "Cancelled");
        await Add(store, "unknown", null, "Unknown");
        await Add(store, "missing-count", null);
        Summary(await Query(Store(), Session), 0, 0, 0, 3, null, null, null);
    }

    [Fact]
    public async Task Query_CursorIsMonotonicSessionScopedAndBoundsAllRowsIncludingUnknowns()
    {
        var store = await Initialized();
        var other = SessionId.From("other");
        await store.SaveAsync(await store.GetOrCreateAsync(other, AgentId.From("agent")));
        for (var i = 0; i < 7; i++)
        {
            await Add(store, $"own-{i}", i % 2 == 0 ? null : i, i % 2 == 0 ? "Running" : "Completed", terminal: i % 2 != 0);
            await Record(store, other, Evidence(store, $"other-{i}", 999, "Completed", terminal: true));
        }
        var seen = new List<long>();
        long cursor = 0;
        for (var pageIndex = 0; pageIndex < 4; pageIndex++)
        {
            var page = await Query(Store(), Session, 2, cursor);
            var rows = Rows(page);
            rows.Length.ShouldBe(pageIndex < 3 ? 2 : 1);
            rows.ShouldAllBe(row => RunId(row).StartsWith("own-", StringComparison.Ordinal));
            var sequences = rows.Select(row => Number(row, "Sequence")).ToArray();
            sequences.ShouldAllBe(sequence => sequence > cursor);
            sequences.ShouldBeInOrder(SortDirection.Ascending);
            seen.AddRange(sequences);
            page.GetProperty("HasMore").GetBoolean().ShouldBe(pageIndex < 3);
            cursor = Number(page, "NextCursor");
            cursor.ShouldBe(sequences.Last());
        }
        seen.Count.ShouldBe(7);
        seen.ShouldBeUnique();
        Rows(await Query(store, Session, 2, cursor)).ShouldBeEmpty();
        Rows(await Query(store, SessionId.From("absent"))).ShouldBeEmpty();
        Summary(await Query(store, other), 7, 0, 0, 0, 999, 999, 999);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task Query_InvalidLimit_RejectsInsteadOfSilentlyUnbounded(int limit)
    {
        var store = await Initialized();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => { _ = await Query(store, Session, limit); });
    }

    [Fact]
    public async Task Query_NegativeCursor_RejectsInvalidSequence()
    {
        var store = await Initialized();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => { _ = await Query(store, Session, after: -1); });
    }

    [Fact]
    public async Task Query_LegacyNormalizedSample_IsBoundedTruncatedAndNeverInventsRuns()
    {
        var store = await Initialized();
        var session = (await store.GetAsync(Session)).ShouldNotBeNull();
        for (var i = 0; i < 1005; i++)
        {
            session.AddEntries([
                new SessionEntry { Role = MessageRole.Tool, Kind = MessageKind.ToolStart, ToolCallId = $"legacy-{i}", ToolName = "probe", ToolArgs = "private-payload-4796", Content = "start" },
                new SessionEntry { Role = MessageRole.Tool, Kind = MessageKind.ToolResult, ToolCallId = $"legacy-{i}", ToolName = "probe", Content = "private-payload-4796" }
            ]);
        }
        await store.SaveAsync(session);
        var page = await Query(Store(), Session, 1);
        Rows(page).ShouldBeEmpty("legacy invocation rows must not be inferred into measured runs");
        Number(page, "LegacySampleCount").ShouldBe(1000, "sample normalized invocations, not both transcript rows");
        Number(page, "UnknownLegacyCount").ShouldBe(1000, "unknown run identity applies even to successful old tool results");
        page.GetProperty("LegacySampleTruncated").GetBoolean().ShouldBeTrue();
        Summary(page, 0, 0, 0, 0, null, null, null);
        page.GetRawText().ShouldNotContain("private-payload-4796");
        Rows(await Query(Store(), Session)).ShouldBeEmpty("opening a store must not census or backfill inferred runs");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public async Task Query_LegacySampleAtBoundary_DoesNotClaimTruncation(int count)
    {
        var store = await Initialized();
        var session = (await store.GetAsync(Session)).ShouldNotBeNull();
        for (var i = 0; i < count; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Kind = MessageKind.ToolResult, ToolCallId = $"legacy-{i}", Content = "old" });
        await store.SaveAsync(session);
        var page = await Query(Store(), Session);
        Number(page, "LegacySampleCount").ShouldBe(count);
        Number(page, "UnknownLegacyCount").ShouldBe(count);
        page.GetProperty("LegacySampleTruncated").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Migration_PreEvidenceDatabase_AddsEmptyRunTableWithoutLegacyCensusOrIdentityInference()
    {
        var store = await Initialized();
        var session = (await store.GetAsync(Session)).ShouldNotBeNull();
        session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Kind = MessageKind.ToolResult, ToolCallId = "old", Content = "old payload" });
        await store.SaveAsync(session);
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE IF EXISTS agent_runs";
            await command.ExecuteNonQueryAsync();
        }
        var reopened = Store();
        var page = await Query(reopened, Session);
        Rows(page).ShouldBeEmpty();
        Number(page, "LegacySampleCount").ShouldBe(1);
        Number(page, "UnknownLegacyCount").ShouldBe(1);
        var old = (await reopened.GetAsync(Session)).ShouldNotBeNull().GetHistorySnapshot().ShouldHaveSingleItem();
        old.Content.ShouldBe("old payload");
        old.AgentRunId.ShouldBeNull();
        await Add(reopened, "after-migration", 2);
        RunId(Rows(await Query(Store(), Session)).ShouldHaveSingleItem()).ShouldBe("after-migration");
    }

    [Fact]
    public async Task Query_MaximumLimit_BoundsUnknownRowsAndReportsOneMoreWithoutReturningIt()
    {
        var store = await Initialized();
        for (var i = 0; i < 1001; i++) await Add(store, $"unknown-{i}", null, "Running", terminal: false);
        var page = await Query(Store(), Session, 1000);
        var rows = Rows(page);
        rows.Length.ShouldBe(1000);
        Summary(page, 0, 0, 0, 1000, null, null, null);
        page.GetProperty("HasMore").GetBoolean().ShouldBeTrue();
        var next = await Query(Store(), Session, 1000, Number(page, "NextCursor"));
        Rows(next).ShouldHaveSingleItem();
        next.GetProperty("HasMore").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_RemovesDurableRunsAndLateAcknowledgementCannotResurrectSession()
    {
        var store = await Initialized();
        await Add(store, "delete-me", 4);
        Rows(await Query(store, Session)).ShouldHaveSingleItem();
        await store.DeleteAsync(Session);
        try { await Add(Store(), "delete-me", 4); }
        catch (InvalidOperationException) { }
        try { await Add(Store(), "late-new-run", 9); }
        catch (InvalidOperationException) { }
        (await Store().GetAsync(Session)).ShouldBeNull();
        Rows(await Query(Store(), Session)).ShouldBeEmpty();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agent_runs WHERE session_id='measured'";
        Convert.ToInt64(await command.ExecuteScalarAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Schema_AdditiveRunTable_HasUniqueSessionRunAndIndexedMonotonicCursor()
    {
        var store = await Initialized();
        await Add(store, "indexed", 4);
        RunId(Rows(await Query(Store(), Session)).ShouldHaveSingleItem()).ShouldBe("indexed");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('agent_runs')";
        var columns = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        columns.Order().ShouldBe(new[] { "sequence", "session_id", "agent_run_id", "started_at", "completed_at", "outcome", "completed_result_count", "guard_observations_json" }.Order());
        command.CommandText = "EXPLAIN QUERY PLAN SELECT sequence FROM agent_runs WHERE session_id='measured' AND sequence>0 ORDER BY sequence LIMIT 2";
        var details = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        details.ShouldContain(detail => detail.Contains("idx_agent_runs_session_sequence", StringComparison.Ordinal));
        details.ShouldAllBe(detail => !detail.Contains("TEMP B-TREE", StringComparison.Ordinal));
        command.CommandText = "INSERT INTO agent_runs(session_id,agent_run_id,started_at,completed_at,outcome,completed_result_count,guard_observations_json) SELECT session_id,agent_run_id,started_at,completed_at,outcome,completed_result_count,guard_observations_json FROM agent_runs WHERE session_id='measured' AND agent_run_id='indexed'";
        var duplicate = await Should.ThrowAsync<SqliteException>(async () => { _ = await command.ExecuteNonQueryAsync(); });
        duplicate.SqliteExtendedErrorCode.ShouldBe(2067, "must fail UNIQUE(session_id,agent_run_id), not an unrelated NOT NULL constraint");
    }

    [Fact]
    public async Task Query_GuardEvidenceIsBoundedAndCustomClassifierKindCannotExportPayload()
    {
        var store = await Initialized();
        var guards = Enumerable.Range(0, 40).Select(_ => Guard("warning", kind: "private-payload-4796 /private/path") with
        {
            EvidenceReferences = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid().ToString("N")).ToArray()
        }).ToArray();
        await Add(store, "bounded", 4, guards: guards);
        var page = await Query(Store(), Session);
        var exported = Rows(page).ShouldHaveSingleItem().GetProperty("GuardObservations");
        exported.GetArrayLength().ShouldBeInRange(1, 16);
        foreach (var guard in exported.EnumerateArray())
        {
            Fields(guard, ["GuardKind", "ConsecutiveCount", "TotalResults", "WarningThreshold", "StopThreshold", "AbsoluteLimitReached", "Disposition", "EvidenceReferences"]);
            guard.GetProperty("GuardKind").GetString().ShouldBe("classified-non-progress");
            guard.GetProperty("EvidenceReferences").GetArrayLength().ShouldBeInRange(0, 16);
        }
        page.GetRawText().ShouldNotContain("private-payload-4796");
        page.GetRawText().ShouldNotContain("/private/path");
        Summary(page, 1, 0, 0, 0, 4, 4, 4);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
