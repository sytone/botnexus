using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Audit;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>Partial legacy argument cleanup must preserve the reconstructed history projection.</summary>
public sealed class PartialToolArgumentCleanupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), nameof(PartialToolArgumentCleanupTests), Guid.NewGuid().ToString("N"));
    private readonly InMemoryConversationStore _conversations = new();
    private string ConnectionString => $"Data Source={Path.Combine(_directory, "sessions.db")};Pooling=False";

    public PartialToolArgumentCleanupTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task CleanupPartialDuplicateArguments_ClearsOnlyExactResultCopyAndRoundTripsProjection()
    {
        const string arguments = "{\"path\":\"資料/α.txt\",\"label\":\"café 🔬\"}";
        await SeedToolCallsAsync(("exact", arguments, arguments));
        await MakePartialAsync("exact", arguments);

        var beforeHistory = await ReadHistoryAsync("partial-exact");
        beforeHistory.Select(entry => entry.ToolArgs).ShouldBe([arguments, arguments]);

        var report = CreateStore().CleanupLegacyToolPayloads(10);

        report.CleanedInvocations.ShouldBe(1);
        report.CleanedRows.ShouldBe(1);
        report.ClearedBytes.ShouldBe(Encoding.UTF8.GetByteCount(arguments));
        report.HasMore.ShouldBeFalse();
        await AssertStoredResultArgumentsAsync("exact", null, marker: 1);
        (await ReadHistoryAsync("partial-exact")).ShouldBe(beforeHistory);

        // Restart through a new store instance and retry: marker-backed arguments remain visible,
        // while a second cleanup is a true no-op.
        var restarted = await ReadHistoryAsync("partial-exact");
        restarted.ShouldBe(beforeHistory);
        var retry = CreateStore().CleanupLegacyToolPayloads(10);
        retry.CleanedInvocations.ShouldBe(0);
        retry.CleanedRows.ShouldBe(0);
        retry.ClearedBytes.ShouldBe(0);
        (await ReadHistoryAsync("partial-exact")).ShouldBe(beforeHistory);
    }

    [Fact]
    public async Task CleanupPartialDuplicateArguments_PreservesDistinctNearAndAbsentArguments()
    {
        const string invocationArguments = "{\"value\":1}";
        const string distinctArguments = "{\"value\":2}";
        const string nearArguments = "{\"value\":1 }";
        const string jsonEquivalentButNotIdentical = "{ \"value\" : 1 }";
        await SeedToolCallsAsync(
            ("distinct", invocationArguments, distinctArguments),
            ("near", invocationArguments, nearArguments),
            ("json-equivalent", invocationArguments, jsonEquivalentButNotIdentical),
            ("absent", invocationArguments, null));
        foreach (var call in new[] { "distinct", "near", "json-equivalent", "absent" })
            await MakePartialAsync(call, call == "absent" ? null : call switch
            {
                "distinct" => distinctArguments,
                "near" => nearArguments,
                _ => jsonEquivalentButNotIdentical
            });

        var before = await ReadRawRowsAsync();
        var report = CreateStore().CleanupLegacyToolPayloads(10);

        report.CleanedInvocations.ShouldBe(0);
        report.CleanedRows.ShouldBe(0);
        report.ClearedBytes.ShouldBe(0);
        (await ReadRawRowsAsync()).ShouldBe(before);
        await AssertStoredResultArgumentsAsync("distinct", distinctArguments, marker: 0);
        await AssertStoredResultArgumentsAsync("near", nearArguments, marker: 0);
        await AssertStoredResultArgumentsAsync("json-equivalent", jsonEquivalentButNotIdentical, marker: 0);
        await AssertStoredResultArgumentsAsync("absent", null, marker: 0);
        foreach (var call in new[] { "distinct", "near", "json-equivalent", "absent" })
        {
            (await ReadHistoryAsync($"partial-{call}")).Length.ShouldBe(2);
        }
    }

    [Fact]
    public async Task CleanupPartialDuplicateArguments_RollsBackWhenMarkerWriteFails()
    {
        const string arguments = "{\"rollback\":\"protect evidence\"}";
        await SeedToolCallsAsync(("rollback", arguments, arguments));
        await MakePartialAsync("rollback", arguments);
        var before = await ReadRawRowsAsync();
        var beforeInvocations = await ReadInvocationRowsAsync();

        await using (var connection = await OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_partial_marker BEFORE UPDATE ON session_history
                WHEN NEW.message_kind='tool-result' AND NEW.tool_args IS NULL AND NEW.tool_args_from_invocation=1
                BEGIN SELECT RAISE(ABORT, 'injected marker failure'); END
                """;
            await command.ExecuteNonQueryAsync();
        }

        var failure = Should.Throw<SqliteException>(() => CreateStore().CleanupLegacyToolPayloads(10));
        failure.Message.ShouldContain("injected marker failure");
        (await ReadRawRowsAsync()).ShouldBe(before);
        (await ReadInvocationRowsAsync()).ShouldBe(beforeInvocations);
    }

    [Fact]
    public async Task CleanupPartialDuplicateArguments_ProcessesOnlyTheRequestedBatch()
    {
        const int callCount = 5;
        const string arguments = "{\"bounded\":\"α payload\"}";
        var calls = Enumerable.Range(0, callCount)
            .Select(index => (Call: $"bounded-{index}", InvocationArguments: arguments, ResultArguments: (string?)arguments)).ToArray();
        await SeedToolCallsAsync(calls);
        foreach (var call in calls)
            await MakePartialAsync(call.Item1, arguments);

        long cursor = 0;
        var totalCleaned = 0;
        for (var index = 0; index < callCount; index++)
        {
            var report = CreateStore().CleanupLegacyToolPayloads(1, cursor);
            report.ScannedInvocations.ShouldBeLessThanOrEqualTo(1);
            report.CleanedInvocations.ShouldBe(1);
            report.CleanedRows.ShouldBe(1);
            report.ClearedBytes.ShouldBe(Encoding.UTF8.GetByteCount(arguments));
            cursor = report.LastScannedInvocationId;
            totalCleaned += report.CleanedInvocations;
            report.HasMore.ShouldBe(index < callCount - 1);
        }

        totalCleaned.ShouldBe(callCount);
        await using var verify = await OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM session_history WHERE tool_args IS NOT NULL AND message_kind='tool-result'";
        Convert.ToInt64(await command.ExecuteScalarAsync()).ShouldBe(0);
        command.CommandText = "SELECT COUNT(*) FROM session_history WHERE message_kind='tool-result' AND tool_args_from_invocation=1";
        Convert.ToInt64(await command.ExecuteScalarAsync()).ShouldBe(callCount);
    }

    private async Task SeedToolCallsAsync(params (string Call, string InvocationArguments, string? ResultArguments)[] calls)
    {
        var store = CreateStore();
        var sink = new DefaultToolAuditSink();
        foreach (var call in calls)
        {
            var session = await store.GetOrCreateAsync(SessionId.From("partial-" + call.Call), AgentId.From("agent"));
            session.AddEntries([
                sink.ProjectStart(call.Call, "probe", call.InvocationArguments),
                sink.ProjectResult(call.Call, "probe", "result " + call.Call, false, 0, call.ResultArguments)
            ]);
            await store.SaveAsync(session);
        }
    }

    private async Task MakePartialAsync(string call, string? resultArguments)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE session_history AS h SET
                tool_args=CASE WHEN h.message_kind='tool-start' THEN NULL ELSE $resultArguments END,
                tool_args_from_invocation=CASE WHEN h.message_kind='tool-result' THEN 0 ELSE h.tool_args_from_invocation END,
                content=CASE WHEN h.message_kind='tool-result' THEN NULL ELSE h.content END
            WHERE h.tool_call_id=$call
            """;
        command.Parameters.AddWithValue("$resultArguments", (object?)resultArguments ?? DBNull.Value);
        command.Parameters.AddWithValue("$call", call);
        Convert.ToInt32(await command.ExecuteNonQueryAsync()).ShouldBe(2);
    }

    private async Task AssertStoredResultArgumentsAsync(string call, string? expectedArguments, int marker)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tool_args,tool_args_from_invocation FROM session_history WHERE tool_call_id=$call AND message_kind='tool-result'";
        command.Parameters.AddWithValue("$call", call);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        (reader.IsDBNull(0) ? null : reader.GetString(0)).ShouldBe(expectedArguments);
        reader.GetInt32(1).ShouldBe(marker);
        (await reader.ReadAsync()).ShouldBeFalse();
    }

    private async Task<List<string>> ReadRawRowsAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,tool_call_id,message_kind,tool_args,content,tool_args_from_invocation FROM session_history ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index)
                ? "<null>" : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture))));
        }
        return rows;
    }

    private async Task<List<string>> ReadInvocationRowsAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,session_id,tool_call_id,arguments_json,result_content,result_bytes,result_sha256,status,is_error,retention_state FROM tool_invocations ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index)
                ? "<null>" : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture))));
        }
        return rows;
    }

    private async Task<BotNexus.Gateway.Abstractions.Models.SessionEntry[]> ReadHistoryAsync(string sessionId)
    {
        var session = await CreateStore().GetAsync(SessionId.From(sessionId));
        return session?.GetHistorySnapshot().ToArray() ?? [];
    }

    private SqliteSessionStore CreateStore()
        => new(ConnectionString, NullLogger<SqliteSessionStore>.Instance, _conversations);

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolForConnectionString(ConnectionString);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
