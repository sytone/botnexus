using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Regression coverage for the #1628 bulk-insert hoist in
/// <c>SqliteSessionStore.ReplaceHistoryAsync</c>. The DELETE-then-INSERT loop was
/// refactored to prepare the <c>INSERT</c> command and its parameters once and only
/// reset <c>.Value</c> per row. A correct hoist must reset EVERY row-varying parameter
/// on EVERY iteration; a stale shared parameter would let an earlier row's value bleed
/// into a later row. These tests round-trip a multi-entry history whose per-row values
/// differ across every nullable/bool/enum column so a single un-reset parameter would
/// corrupt a neighbouring row and fail the assertion.
/// </summary>
public sealed class PrepareBulkInsertSessionTests : IDisposable
{
    private readonly string _directoryPath =
        Path.Combine(AppContext.BaseDirectory, "PrepareBulkInsertSessionTests", Guid.NewGuid().ToString("N"));
    private readonly string _connectionString;
    private readonly InMemoryConversationStore _conversations = new();

    public PrepareBulkInsertSessionTests()
    {
        Directory.CreateDirectory(_directoryPath);
        var databasePath = Path.Combine(_directoryPath, "sessions.db");
        _connectionString = $"Data Source={databasePath};Pooling=False";
    }

    // Each call opens an independent store on the same SQLite file so a save by one
    // instance is observed by a fresh-read instance (no in-process cache hit), which is
    // how the round-trip exercises the real persistence path.
    private SqliteSessionStore CreateStore()
        => new(_connectionString, NullLogger<SqliteSessionStore>.Instance, _conversations);

    [Fact]
    public async Task ReplaceHistory_MultiEntryHistory_RoundTripsEveryFieldPerRow()
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s-bulk"), AgentId.From("agent-a"));

        // A history that spans every discriminating column with DIFFERENT per-row values
        // so a stale hoisted parameter would corrupt a later row:
        //  - entry 0: a plain user message (all tool/flag fields default/null)
        //  - entry 1: a tool call carrying tool_name / tool_call_id / tool_args + ToolIsError
        //  - entry 2: a compaction summary (IsCompactionSummary = true)
        //  - entry 3: a crash sentinel (IsCrashSentinel = true)
        //  - entry 4: an entry with ThinkingContent
        //  - entry 5: an entry with a Trigger (TriggerType.Cron) and IsHistory = true
        session.AddEntries(
        [
            new SessionEntry
            {
                Role = MessageRole.User,
                Content = "hello world",
            },
            new SessionEntry
            {
                Role = MessageRole.Tool,
                Content = "tool-result-body",
                ToolName = "search",
                ToolCallId = "call-123",
                ToolArgs = "{\"q\":\"x\"}",
                ToolIsError = true,
            },
            new SessionEntry
            {
                Role = MessageRole.Assistant,
                Content = "summary-of-earlier-turns",
                IsCompactionSummary = true,
            },
            new SessionEntry
            {
                Role = MessageRole.Notification,
                Content = "crash-sentinel-marker",
                IsCrashSentinel = true,
            },
            new SessionEntry
            {
                Role = MessageRole.Assistant,
                Content = "answer-with-reasoning",
                ThinkingContent = "step-by-step reasoning",
            },
            new SessionEntry
            {
                Role = MessageRole.User,
                Content = "cron-triggered-turn",
                Trigger = TriggerType.Cron,
                IsHistory = true,
            },
        ]);

        await store.SaveAsync(session);

        var reloaded = await CreateStore().GetAsync(SessionId.From("s-bulk"));
        reloaded.ShouldNotBeNull();
        var rows = reloaded!.GetHistorySnapshot().ToList();
        rows.Count.ShouldBe(6);

        // Row 0 - plain user message: every tool/flag field must be cleared, NOT carrying
        // any later row's value.
        rows[0].Role.Value.ShouldBe("user");
        rows[0].Content.ShouldBe("hello world");
        rows[0].ToolName.ShouldBeNull();
        rows[0].ToolCallId.ShouldBeNull();
        rows[0].ToolArgs.ShouldBeNull();
        rows[0].ToolIsError.ShouldBeFalse();
        rows[0].IsCompactionSummary.ShouldBeFalse();
        rows[0].IsCrashSentinel.ShouldBeFalse();
        rows[0].IsHistory.ShouldBeFalse();
        rows[0].Trigger.ShouldBeNull();
        rows[0].ThinkingContent.ShouldBeNull();

        // Row 1 - tool call: tool_name / tool_call_id / tool_args + ToolIsError set; all
        // OTHER discriminators stay clear.
        rows[1].Role.Value.ShouldBe("tool");
        rows[1].Content.ShouldBe("tool-result-body");
        rows[1].ToolName.ShouldBe("search");
        rows[1].ToolCallId.ShouldBe("call-123");
        rows[1].ToolArgs.ShouldBe("{\"q\":\"x\"}");
        rows[1].ToolIsError.ShouldBeTrue();
        rows[1].IsCompactionSummary.ShouldBeFalse();
        rows[1].IsCrashSentinel.ShouldBeFalse();
        rows[1].IsHistory.ShouldBeFalse();
        rows[1].Trigger.ShouldBeNull();
        rows[1].ThinkingContent.ShouldBeNull();

        // Row 2 - compaction summary: IsCompactionSummary true; the tool_* values from row 1
        // must NOT bleed through (this is the stale-parameter trap).
        rows[2].Content.ShouldBe("summary-of-earlier-turns");
        rows[2].IsCompactionSummary.ShouldBeTrue();
        rows[2].ToolName.ShouldBeNull();
        rows[2].ToolCallId.ShouldBeNull();
        rows[2].ToolArgs.ShouldBeNull();
        rows[2].ToolIsError.ShouldBeFalse();
        rows[2].IsCrashSentinel.ShouldBeFalse();
        rows[2].IsHistory.ShouldBeFalse();
        rows[2].Trigger.ShouldBeNull();
        rows[2].ThinkingContent.ShouldBeNull();

        // Row 3 - crash sentinel: IsCrashSentinel true; IsCompactionSummary from row 2 must
        // NOT persist.
        rows[3].Content.ShouldBe("crash-sentinel-marker");
        rows[3].IsCrashSentinel.ShouldBeTrue();
        rows[3].IsCompactionSummary.ShouldBeFalse();
        rows[3].ToolName.ShouldBeNull();
        rows[3].ToolIsError.ShouldBeFalse();
        rows[3].IsHistory.ShouldBeFalse();
        rows[3].Trigger.ShouldBeNull();
        rows[3].ThinkingContent.ShouldBeNull();

        // Row 4 - thinking content present; crash-sentinel flag from row 3 must NOT persist.
        rows[4].Content.ShouldBe("answer-with-reasoning");
        rows[4].ThinkingContent.ShouldBe("step-by-step reasoning");
        rows[4].IsCrashSentinel.ShouldBeFalse();
        rows[4].IsCompactionSummary.ShouldBeFalse();
        rows[4].ToolName.ShouldBeNull();
        rows[4].Trigger.ShouldBeNull();
        rows[4].IsHistory.ShouldBeFalse();

        // Row 5 - trigger + IsHistory; ThinkingContent from row 4 must NOT persist.
        rows[5].Content.ShouldBe("cron-triggered-turn");
        rows[5].Trigger.ShouldNotBeNull();
        rows[5].Trigger!.Value.ShouldBe("cron");
        rows[5].IsHistory.ShouldBeTrue();
        rows[5].ThinkingContent.ShouldBeNull();
        rows[5].IsCrashSentinel.ShouldBeFalse();
        rows[5].IsCompactionSummary.ShouldBeFalse();
        rows[5].ToolName.ShouldBeNull();
    }

    [Fact]
    public async Task ReplaceHistory_OnReSave_ReplacesCleanly_DeleteThenInsert()
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s-resave"), AgentId.From("agent-a"));

        session.AddEntries(
        [
            new SessionEntry { Role = MessageRole.User, Content = "first-a", ToolName = "alpha" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "first-b", IsCompactionSummary = true },
            new SessionEntry { Role = MessageRole.User, Content = "first-c", Trigger = TriggerType.Cron },
        ]);
        await store.SaveAsync(session);

        // Re-save with a completely different history. ReplaceHistory deletes the prior rows
        // then re-inserts; the reload must reflect ONLY the new set (no stale rows, no stale
        // per-row parameter bleed across the new rows).
        var reloaded = await CreateStore().GetAsync(SessionId.From("s-resave"));
        reloaded.ShouldNotBeNull();
        // ReplaceHistory clears the in-memory history and sets the new set; SaveAsync then
        // drives ReplaceHistoryAsync (DELETE-then-INSERT) against SQLite.
        reloaded!.ReplaceHistory(
        [
            new SessionEntry { Role = MessageRole.System, Content = "second-only" },
            new SessionEntry { Role = MessageRole.Tool, Content = "second-tool", ToolName = "beta", ToolCallId = "c-9" },
        ]);
        await store.SaveAsync(reloaded);

        var afterResave = await CreateStore().GetAsync(SessionId.From("s-resave"));
        afterResave.ShouldNotBeNull();
        var rows = afterResave!.GetHistorySnapshot().ToList();
        rows.Count.ShouldBe(2);
        rows[0].Content.ShouldBe("second-only");
        rows[0].ToolName.ShouldBeNull();
        rows[0].Trigger.ShouldBeNull();
        rows[1].Content.ShouldBe("second-tool");
        rows[1].ToolName.ShouldBe("beta");
        rows[1].ToolCallId.ShouldBe("c-9");
        // No stale row from the first save survived.
        rows.ShouldNotContain(r => r.Content == "first-a");
        rows.ShouldNotContain(r => r.Content == "first-b");
        rows.ShouldNotContain(r => r.Content == "first-c");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directoryPath))
            Directory.Delete(_directoryPath, recursive: true);
    }
}
