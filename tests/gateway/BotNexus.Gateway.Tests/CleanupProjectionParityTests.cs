using System.Text;
using System.Text.RegularExpressions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>Backend consumer parity for historical duplicate cleanup, not browser or process-crash coverage.</summary>
public sealed class CleanupProjectionParityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), nameof(CleanupProjectionParityTests), Guid.NewGuid().ToString("N"));
    private readonly InMemoryConversationStore _conversations = new();
    private static readonly SessionId Session = SessionId.From("cleanup-parity");
    private static readonly ConversationId Conversation = ConversationId.From("c_cleanup_parity");
    private static readonly DateTimeOffset Started = new(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Arguments = "{\"path\":\"資料/α.txt\",\"label\":\"café 🔬\"}";
    private const string Result = "résult ✓ 日本語\nsecond line <value> & café";
    private string ConnectionString => $"Data Source={Path.Combine(_directory, "sessions.db")};Pooling=False";

    public CleanupProjectionParityTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task CleanupLegacyToolPayloads_FreshHistoryAndPaginatedRestPreserveVisibleEvidence()
    {
        await SeedLegacyAsync();
        var before = await HistoryAsync();
        before.Length.ShouldBe(9);
        before.Select(entry => entry.PersistenceId).ShouldBeUnique();
        before.ShouldAllBe(entry => entry.PersistenceId.HasValue && entry.PersistenceKey != null);
        before.Select(entry => entry.Content).ShouldBe([
            "hello 日本語 café 🔬", "safe start", "between tools", Result,
            "failed start", "failed: accès refusé", "incomplete start", "finished ✓", "notification β"]);
        before.Select(entry => entry.Timestamp).ShouldBe(Enumerable.Range(0, 9).Select(index => Started.AddSeconds(index)));
        await AssertPagesAsync(before);
        var protectedBefore = await RawRowsAsync("tool_call_id IN ('failed','incomplete')");
        var evidenceBefore = await RawInvocationsAsync();

        await CleanupAndAssertRawAsync();

        var after = await HistoryAsync();
        // Physical duplicates disappear, but invocation-backed arguments preserve the complete
        // visible projection, including persisted identity, ordering and result arguments.
        after.ShouldBe(before);
        after.Single(entry => entry.ToolCallId == "safe" && entry.Kind == MessageKind.ToolStart).ToolArgs.ShouldBe(Arguments);
        after.Single(entry => entry.ToolCallId == "safe" && entry.Kind == MessageKind.ToolResult).Content.ShouldBe(Result);
        await AssertPagesAsync(after);
        (await RawRowsAsync("tool_call_id IN ('failed','incomplete')")).ShouldBe(protectedBefore);
        (await RawInvocationsAsync()).ShouldBe(evidenceBefore);

        await AssertFreshRetryNoWritesAsync();
        (await HistoryAsync()).ShouldBe(after);
        (await RawInvocationsAsync()).ShouldBe(evidenceBefore);
    }

    [Fact]
    public async Task CleanupLegacyToolPayloads_ProductionSessionAndConversationExportsPreserveTranscript()
    {
        await SeedLegacyAsync();
        var before = await ExportsAsync();

        await CleanupAndAssertRawAsync();

        (await ExportsAsync()).ShouldBe(before);
        await AssertFreshRetryNoWritesAsync();
        (await ExportsAsync()).ShouldBe(before);
    }

    private async Task SeedLegacyAsync()
    {
        await _conversations.CreateAsync(new Conversation
        {
            ConversationId = Conversation, AgentId = AgentId.From("quill"), Title = "Cleanup parity café",
            Status = ConversationStatus.Active, CreatedAt = Started, UpdatedAt = Started.AddMinutes(1)
        });
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(Session, AgentId.From("quill"));
        session.Session.ConversationId = Conversation;
        await store.SaveAsync(session);
        // Insert the actual pre-cutover layout, not current typed writes (which are narrow).
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_history(session_id,role,content,timestamp,tool_name,tool_call_id,tool_args,tool_is_error,message_kind,persistence_key,sender_id)
            VALUES($session,$role,$content,$timestamp,$tool,$call,$args,$error,$kind,$key,$sender)
            """;
        var rows = new (string Role, string Content, string? Call, string? Args, bool Error, string Kind)[]
        {
            ("user", "hello 日本語 café 🔬", null, null, false, "message"),
            ("tool", "safe start", "safe", Arguments, false, "tool-start"),
            ("assistant", "between tools", null, null, false, "message"),
            ("tool", Result, "safe", Arguments, false, "tool-result"),
            ("tool", "failed start", "failed", "{\"path\":\"protected β\"}", false, "tool-start"),
            ("tool", "failed: accès refusé", "failed", "{\"path\":\"protected β\"}", true, "tool-result"),
            ("tool", "incomplete start", "incomplete", "{\"path\":\"pending γ\"}", false, "tool-start"),
            ("assistant", "finished ✓", null, null, false, "message"),
            ("notification", "notification β", null, null, false, "message")
        };
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$session", Session.Value);
            command.Parameters.AddWithValue("$role", row.Role);
            command.Parameters.AddWithValue("$content", row.Content);
            command.Parameters.AddWithValue("$timestamp", Started.AddSeconds(index).ToString("O"));
            command.Parameters.AddWithValue("$tool", row.Call is null ? DBNull.Value : "read");
            command.Parameters.AddWithValue("$call", (object?)row.Call ?? DBNull.Value);
            command.Parameters.AddWithValue("$args", (object?)row.Args ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", row.Error ? 1 : 0);
            command.Parameters.AddWithValue("$kind", row.Kind);
            command.Parameters.AddWithValue("$key", $"legacy-{index}");
            command.Parameters.AddWithValue("$sender", row.Role == "user" ? "reader" : DBNull.Value);
            (await command.ExecuteNonQueryAsync()).ShouldBe(1);
        }
        (await RawInvocationsAsync()).ShouldBeEmpty();
        var backfill = store.BackfillLegacyToolInvocations(100);
        backfill.ScannedRows.ShouldBe(5);
        backfill.LinkedRows.ShouldBe(5);
        backfill.InvocationCount.ShouldBe(3);
        backfill.HasMore.ShouldBeFalse();
        (await RawRowsAsync("tool_call_id='safe' AND tool_args IS NOT NULL")).Count.ShouldBe(2);
        await using var statuses = connection.CreateCommand();
        statuses.CommandText = "SELECT status FROM tool_invocations ORDER BY id";
        await using var reader = await statuses.ExecuteReaderAsync();
        var actualStatuses = new List<string>();
        while (await reader.ReadAsync()) actualStatuses.Add(reader.GetString(0));
        actualStatuses.ShouldBe(["success", "error", "incomplete"]);
    }

    private async Task CleanupAndAssertRawAsync()
    {
        var report = CreateStore().CleanupLegacyToolPayloads(100);
        report.CleanedInvocations.ShouldBe(1);
        report.CleanedRows.ShouldBe(2);
        report.ClearedBytes.ShouldBe(2L * Encoding.UTF8.GetByteCount(Arguments) + Encoding.UTF8.GetByteCount(Result));
        report.HasMore.ShouldBeFalse();
        (await RawRowsAsync("tool_call_id='safe' AND tool_invocation_id IS NOT NULL")).Count.ShouldBe(2);
        (await RawRowsAsync("tool_call_id='safe' AND (tool_args IS NOT NULL OR (message_kind='tool-result' AND content IS NOT NULL))")).ShouldBeEmpty();
        (await RawRowsAsync("tool_call_id IN ('failed','incomplete') AND tool_args IS NOT NULL")).Count.ShouldBe(3);
    }

    private async Task<SessionEntry[]> HistoryAsync()
    {
        var session = await CreateStore().GetAsync(Session) ?? throw new InvalidOperationException("Missing fixture session.");
        session.AgentId.ShouldBe(AgentId.From("quill"));
        session.ConversationId.ShouldBe(Conversation);
        return session.GetHistorySnapshot().ToArray();
    }

    private async Task AssertPagesAsync(IReadOnlyList<SessionEntry> expected)
    {
        // New controller/store per page: no warmed history can hide hydration or offset defects.
        var collected = new List<SessionEntry>();
        foreach (var offset in new[] { 0, 2, 4, 6, 8, 10 })
        {
            var response = await new SessionsController(CreateStore()).GetHistory(Session.Value, offset, 2);
            var page = response.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<SessionHistoryResponse>();
            page.Offset.ShouldBe(offset);
            page.Limit.ShouldBe(2);
            page.TotalCount.ShouldBe(expected.Count);
            page.Entries.ShouldBe(expected.Skip(offset).Take(2));
            collected.AddRange(page.Entries);
        }
        collected.ShouldBe(expected);
    }

    private async Task<string[]> ExportsAsync()
    {
        var files = new List<FileContentResult>
        {
            (await new SessionsController(CreateStore(), conversations: _conversations).ExportMarkdown(Session.Value)).ShouldBeOfType<FileContentResult>()
        };
        foreach (var format in new[] { "markdown", "html" })
        {
            files.Add((await new SessionsController(CreateStore(), conversations: _conversations).ExportTranscript(Session.Value, format)).ShouldBeOfType<FileContentResult>());
            files.Add((await new ConversationsController(_conversations, CreateStore()).ExportTranscript(Conversation.Value, format)).ShouldBeOfType<FileContentResult>());
        }
        return files.Select(file =>
        {
            var text = Encoding.UTF8.GetString(file.FileContents);
            // HtmlEncode may use numeric entities for non-ASCII text; decode only for presence checks.
            var visible = System.Net.WebUtility.HtmlDecode(text);
            visible.ShouldContain("hello 日本語 café 🔬");
            visible.ShouldContain("résult ✓ 日本語");
            visible.ShouldContain("資料/α.txt");
            visible.ShouldContain("failed: accès refusé");
            visible.ShouldContain("pending γ");
            visible.ShouldContain("finished ✓");
            visible.ShouldContain("Tool Result");
            visible.ShouldContain("Tool Error");
            if (files.IndexOf(file) > 0)
            {
                if (file.ContentType == "text/html")
                    text.ShouldContain("<dt>Tool calls</dt><dd>3</dd>");
                else
                    text.ShouldContain("- **Tool calls:** 3");
            }
            // Controllers construct the real assembler with the system clock. Exclude ONLY its
            // generated-at metadata, not entry timestamps, identities, payloads, ordering or counts.
            text = Regex.Replace(text, @"(?m)^- \*\*Exported:\*\* \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} UTC\r?$", "- **Exported:** <clock>");
            text = Regex.Replace(text, @"<dt>Exported</dt><dd>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} UTC</dd>", "<dt>Exported</dt><dd><clock></dd>");
            return $"{file.ContentType}\n{text}";
        }).ToArray();
    }

    private async Task AssertFreshRetryNoWritesAsync()
    {
        var before = await RawRowsAsync("1=1");
        await using var connection = await OpenAsync();
        await using var guard = connection.CreateCommand();
        guard.CommandText = """
            CREATE TRIGGER reject_parity_history_rewrite BEFORE UPDATE ON session_history
            BEGIN SELECT RAISE(ABORT, 'unexpected history rewrite'); END;
            CREATE TRIGGER reject_parity_evidence_rewrite BEFORE UPDATE ON tool_invocations
            BEGIN SELECT RAISE(ABORT, 'unexpected invocation rewrite'); END;
            """;
        await guard.ExecuteNonQueryAsync();
        var retry = CreateStore().CleanupLegacyToolPayloads(100);
        retry.ScannedInvocations.ShouldBe(0);
        retry.CleanedInvocations.ShouldBe(0);
        retry.CleanedRows.ShouldBe(0);
        retry.ClearedBytes.ShouldBe(0);
        retry.OrphanedInvocationsDeleted.ShouldBe(0);
        retry.HasMore.ShouldBeFalse();
        (await RawRowsAsync("1=1")).ShouldBe(before);
    }

    private Task<List<string>> RawRowsAsync(string predicate) => RawTableAsync("session_history", predicate);
    private Task<List<string>> RawInvocationsAsync() => RawTableAsync("tool_invocations", "1=1");

    private async Task<List<string>> RawTableAsync(string table, string predicate)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} WHERE {predicate} ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            // Preserve all column bytes and NULLs, including links and persistence identity.
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index)
                ? "NULL" : Convert.ToHexString(Encoding.UTF8.GetBytes(reader.GetValue(index).ToString() ?? string.Empty)))));
        }
        return rows;
    }

    private SqliteSessionStore CreateStore() => new(ConnectionString, NullLogger<SqliteSessionStore>.Instance, _conversations);
    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolForConnectionString(ConnectionString);
        Directory.Delete(_directory, recursive: true);
    }
}
