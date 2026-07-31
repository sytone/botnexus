using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Services;
using BotNexus.Gateway.Tools;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Proves the bounded re-read-and-retry policy (#2131) that every tool which round-trips the whole
/// <see cref="Conversation"/> aggregate through <c>SaveAsync</c> now shares.
/// </summary>
/// <remarks>
/// Since #2471 the SQLite store guards <c>SaveAsync</c> with a version compare-and-swap. These tests
/// use a CAS-enforcing decorator over <see cref="InMemoryConversationStore"/> so they exercise the
/// same contract without needing a real database. The load-bearing assertion is not "no exception
/// was thrown" - it is that the concurrent writer's field (the pin) SURVIVES the retry, which is
/// only true if the retry recomputes from fresh state instead of replaying the stale snapshot.
/// </remarks>
public sealed class ConversationSaveRetryTests
{
    // ── TodoTool ────────────────────────────────────────────────────────

    [Fact]
    public async Task TodoWrite_RetriesAndPreservesConcurrentWritersPin()
    {
        var (store, convId) = await NewStoreAsync();
        var tool = new TodoTool(convId, store);

        // A concurrent writer pins the conversation exactly once, after the tool has read but
        // before its first save commits. That bumps the version and makes the tool's save lose.
        store.InterleaveOnce(s => s.PinAsync(convId, pin: true));

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "write",
            ["items"] = ItemsJson("""[{ "text": "alpha" }]"""),
        });

        ReadText(result).ShouldContain("Todo list set with 1 item(s)");
        store.SaveAttempts.ShouldBe(2, "the first save must lose the CAS race and be retried");

        var conv = await store.GetAsync(convId);
        conv.ShouldNotBeNull();
        TodoTool.Parse(conv.TodoJson).Single().Text.ShouldBe("alpha");
        conv.IsPinned.ShouldBeTrue("the concurrent writer's pin must survive the retry");
    }

    [Fact]
    public async Task TodoUpdate_RetriesAgainstFreshTodoStateWrittenByAnotherWriter()
    {
        var (store, convId) = await NewStoreAsync();
        var tool = new TodoTool(convId, store);

        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "write",
            ["items"] = ItemsJson("""[{ "id": "a", "text": "task a" }]"""),
        });

        // Another writer both pins AND appends an unrelated todo item between the tool's read and
        // its save. Replaying the stale aggregate would erase item "b" entirely.
        store.InterleaveOnce(async s =>
        {
            await s.PinAsync(convId, pin: true);
            var fresh = await s.GetAsync(convId);
            var items = TodoTool.Parse(fresh!.TodoJson);
            items.Add(new TodoTool.TodoItem
            {
                Id = "b",
                Text = "task b",
                Status = "pending",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await s.ForceSaveAsync(fresh with
            {
                TodoJson = JsonSerializer.Serialize(
                    new TodoTool.TodoDocument { Items = [.. items] },
                    TodoTool.TodoJsonOptions),
            });
        });

        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["id"] = "a",
            ["status"] = "done",
        });

        var conv = await store.GetAsync(convId);
        var persisted = TodoTool.Parse(conv!.TodoJson);
        persisted.Single(i => i.Id == "a").Status.ShouldBe("done", "the tool's own intent must be applied");
        persisted.ShouldContain(i => i.Id == "b", "the concurrent writer's todo item must survive the retry");
        conv.IsPinned.ShouldBeTrue("the concurrent writer's pin must survive the retry");
    }

    [Fact]
    public async Task TodoWrite_SurfacesActionableErrorWhenRetriesAreExhausted()
    {
        var (store, convId) = await NewStoreAsync();
        var tool = new TodoTool(convId, store);

        // Conflict on EVERY attempt - the retry must terminate, not spin forever.
        store.InterleaveAlways(s => s.PinAsync(convId, pin: true));

        var ex = await Should.ThrowAsync<ConversationConcurrencyException>(() => ExecuteAsync(tool,
            new Dictionary<string, object?>
            {
                ["action"] = "write",
                ["items"] = ItemsJson("""[{ "text": "alpha" }]"""),
            }));

        ex.ConversationId.ShouldBe(convId.Value);
        ex.Message.ShouldContain("modified by another writer");
        store.SaveAttempts.ShouldBe(ConversationSaveRetry.MaxAttempts, "attempts must be bounded");
    }

    // ── AskUserTool ─────────────────────────────────────────────────────

    [Fact]
    public async Task AskUserPersistAndClear_RetryAndPreserveConcurrentWritersPin()
    {
        var (store, convId) = await NewStoreAsync();
        var registry = new AskUserResponseRegistry();
        var tool = new AskUserTool(
            registry,
            AgentId.From("agent-a"),
            SessionId.From("session-1"),
            convId,
            store);

        // Gate: released once the pending prompt has been durably persisted, so the assertion below
        // observes a real committed state rather than racing the tool. No sleeps.
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.OnSaved(c =>
        {
            if (c.PendingAskUserJson is not null)
                persisted.TrySetResult();
        });

        store.InterleaveOnce(s => s.PinAsync(convId, pin: true));

        var updates = new List<AgentToolResult>();
        var arguments = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "Which environment?",
        });
        var execution = tool.ExecuteAsync("call-1", arguments, onUpdate: updates.Add);

        await persisted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var pending = await store.GetAsync(convId);
        pending!.PendingAskUserJson.ShouldNotBeNull("the register path must persist despite the CAS conflict");
        pending.IsPinned.ShouldBeTrue("the concurrent writer's pin must survive the register retry");

        // Now conflict again so the CLEAR path must also retry, and prove the pin still survives.
        store.InterleaveOnce(s => s.PinAsync(convId, pin: true));

        var request = updates[^1].Details as AskUserRequest;
        request.ShouldNotBeNull();
        registry.TryComplete(request.ConversationId, request.RequestId, new AskUserResponse
        {
            RequestId = request.RequestId,
            FreeFormText = "staging",
        }).ShouldBeTrue();
        await execution.WaitAsync(TimeSpan.FromSeconds(10));

        var cleared = await store.GetAsync(convId);
        cleared!.PendingAskUserJson.ShouldBeNull("the clear path must commit despite the CAS conflict");
        cleared.IsPinned.ShouldBeTrue("the concurrent writer's pin must survive the clear retry");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static async Task<(CasConversationStore store, ConversationId convId)> NewStoreAsync()
    {
        var store = new CasConversationStore();
        var convId = ConversationId.Create();
        await store.CreateAsync(new Conversation
        {
            ConversationId = convId,
            AgentId = AgentId.From("agent-a"),
            Title = "retry test",
        });
        return (store, convId);
    }

    private static async Task<AgentToolResult> ExecuteAsync(TodoTool tool, Dictionary<string, object?> args)
    {
        var prepared = await tool.PrepareArgumentsAsync(args);
        return await tool.ExecuteAsync("call-1", prepared);
    }

    private static object ItemsJson(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(c => c.Type == AgentToolContentType.Text).Value;
}
