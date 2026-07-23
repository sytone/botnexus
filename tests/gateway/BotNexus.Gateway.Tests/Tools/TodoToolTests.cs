using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Tools;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class TodoToolTests
{
    private static (TodoTool tool, InMemoryConversationStore store, ConversationId convId) NewTool()
    {
        var store = new InMemoryConversationStore();
        var convId = ConversationId.Create();
        store.CreateAsync(new Conversation
        {
            ConversationId = convId,
            AgentId = AgentId.From("agent-a"),
            Title = "Todo test",
        }).GetAwaiter().GetResult();
        return (new TodoTool(convId, store), store, convId);
    }

    [Fact]
    public void Tool_HasExpectedNameAndLabel()
    {
        var tool = new TodoTool(null, null);
        tool.Name.ShouldBe("todo");
        tool.Label.ShouldBe("Todo");
    }

    [Fact]
    public void Tool_DescriptionDistinguishesExecutionChecklistFromDurableSystems()
    {
        var description = new TodoTool(null, null).Definition.Description;

        // The todo tool is the agent's own execution checklist for its work loop; it must stay
        // generic and NOT name any particular external task/work-tracking system (#2071).
        description.ShouldContain("per-conversation execution checklist", Case.Insensitive);
        description.ShouldContain("loop or set of loops", Case.Insensitive);
        description.ShouldContain("not a durable or user-facing", Case.Insensitive);
        description.ShouldContain("source of truth", Case.Insensitive);
        description.ShouldContain("ownership", Case.Insensitive);
        description.ShouldContain("Do not substitute this list", Case.Insensitive);
        description.ShouldContain("verified by a tool result", Case.Insensitive);
        description.ShouldNotContain("TaskNexus", Case.Insensitive);
    }

    // ── write happy path ────────────────────────────────────────────────

    [Fact]
    public async Task Write_PersistsItemsOnConversation()
    {
        var (tool, store, convId) = NewTool();

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "write",
            ["items"] = ItemsJson("""[{ "text": "first task" }, { "text": "second task", "status": "in_progress" }]"""),
        });

        ReadText(result).ShouldContain("Todo list set with 2 item(s)");
        var conv = await store.GetAsync(convId);
        conv!.TodoJson.ShouldNotBeNull();
        var items = TodoTool.Parse(conv.TodoJson);
        items.Count.ShouldBe(2);
        items[0].Text.ShouldBe("first task");
        items[0].Status.ShouldBe("pending");
        items[1].Status.ShouldBe("in_progress");
        items[0].Id.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Write_SkipsBlankRows()
    {
        var (tool, store, convId) = NewTool();

        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "write",
            ["items"] = ItemsJson("""[{ "text": "real" }, { "text": "   " }, { "text": "" }]"""),
        });

        var conv = await store.GetAsync(convId);
        TodoTool.Parse(conv!.TodoJson).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Write_NormalizesUnknownStatusToPending()
    {
        var (tool, store, convId) = NewTool();

        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "write",
            ["items"] = ItemsJson("""[{ "text": "x", "status": "bogus" }]"""),
        });

        var items = TodoTool.Parse((await store.GetAsync(convId))!.TodoJson);
        items.Single().Status.ShouldBe("pending");
    }

    // ── update happy path ───────────────────────────────────────────────

    [Fact]
    public async Task Update_ChangesStatusAndTextById()
    {
        var (tool, store, convId) = NewTool();
        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "write",
            ["items"] = ItemsJson("""[{ "id": "a1", "text": "do thing" }]"""),
        });

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["id"] = "a1",
            ["text"] = "did thing",
            ["status"] = "done",
        });

        ReadText(result).ShouldContain("Updated todo item 'a1'");
        var item = TodoTool.Parse((await store.GetAsync(convId))!.TodoJson).Single();
        item.Text.ShouldBe("did thing");
        item.Status.ShouldBe("done");
    }

    [Fact]
    public async Task Update_UnknownId_AppendsWhenTextProvided()
    {
        var (tool, store, convId) = NewTool();
        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "write",
            ["items"] = ItemsJson("""[{ "id": "a1", "text": "existing" }]"""),
        });

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["id"] = "new-id",
            ["text"] = "appended task",
        });

        ReadText(result).ShouldContain("Appended new todo item 'new-id'");
        TodoTool.Parse((await store.GetAsync(convId))!.TodoJson).Count.ShouldBe(2);
    }

    // ── sad paths ───────────────────────────────────────────────────────

    [Fact]
    public async Task Update_UnknownId_NoText_ReturnsErrorWithoutAppending()
    {
        var (tool, store, convId) = NewTool();
        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "write",
            ["items"] = ItemsJson("""[{ "id": "a1", "text": "existing" }]"""),
        });

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["id"] = "ghost",
        });

        ReadText(result).ShouldContain("'text' is required");
        TodoTool.Parse((await store.GetAsync(convId))!.TodoJson).Count.ShouldBe(1);
    }

    [Fact]
    public async Task PrepareArguments_MalformedAction_Throws()
    {
        var tool = new TodoTool(ConversationId.Create(), new InMemoryConversationStore());
        await Should.ThrowAsync<ArgumentException>(async () =>
            await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["action"] = "frobnicate" }));
    }

    [Fact]
    public async Task PrepareArguments_WriteWithoutItems_Throws()
    {
        var tool = new TodoTool(ConversationId.Create(), new InMemoryConversationStore());
        await Should.ThrowAsync<ArgumentException>(async () =>
            await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["action"] = "write" }));
    }

    [Fact]
    public async Task PrepareArguments_UpdateWithoutId_Throws()
    {
        var tool = new TodoTool(ConversationId.Create(), new InMemoryConversationStore());
        await Should.ThrowAsync<ArgumentException>(async () =>
            await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["action"] = "update" }));
    }

    [Fact]
    public async Task NoConversation_NoOps()
    {
        var tool = new TodoTool(null, null);
        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["action"] = "list" });
        ReadText(result).ShouldContain("no conversation context");
    }

    [Fact]
    public async Task ConversationMissingFromStore_ReturnsNotFound()
    {
        // Tool points at a conversation id that was never created in the store.
        var tool = new TodoTool(ConversationId.Create(), new InMemoryConversationStore());
        var result = await ExecuteAsync(tool, new Dictionary<string, object?> { ["action"] = "list" });
        ReadText(result).ShouldContain("Conversation not found");
    }

    // ── list / clear ────────────────────────────────────────────────────

    [Fact]
    public async Task List_EmptyAndPopulated()
    {
        var (tool, _, _) = NewTool();

        ReadText(await ExecuteAsync(tool, new Dictionary<string, object?> { ["action"] = "list" }))
            .ShouldContain("Todo list is empty");

        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "write",
            ["items"] = ItemsJson("""[{ "text": "alpha", "status": "done" }]"""),
        });

        var listed = ReadText(await ExecuteAsync(tool, new Dictionary<string, object?> { ["action"] = "list" }));
        listed.ShouldContain("[x] alpha");
    }

    [Fact]
    public async Task Clear_EmptiesTheList()
    {
        var (tool, store, convId) = NewTool();
        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "write",
            ["items"] = ItemsJson("""[{ "text": "to remove" }]"""),
        });

        var result = await ExecuteAsync(tool, new Dictionary<string, object?> { ["action"] = "clear" });

        ReadText(result).ShouldContain("Todo list cleared");
        (await store.GetAsync(convId))!.TodoJson.ShouldBeNull();
    }

    // ── persistence across sessions (new tool instance, same store) ──────

    [Fact]
    public async Task State_PersistsAcrossToolInstances()
    {
        var (tool, store, convId) = NewTool();
        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "write",
            ["items"] = ItemsJson("""[{ "id": "p1", "text": "persisted" }]"""),
        });

        // A fresh tool instance (simulating a later session) pointed at the same store + conversation.
        var laterTool = new TodoTool(convId, store);
        var listed = ReadText(await ExecuteAsync(laterTool, new Dictionary<string, object?> { ["action"] = "list" }));
        listed.ShouldContain("persisted");
        listed.ShouldContain("id=p1");
    }

    // ── malformed persisted payload is tolerated ────────────────────────

    [Fact]
    public async Task Parse_ToleratesCorruptJson()
    {
        TodoTool.Parse("not json at all").ShouldBeEmpty();
        TodoTool.Parse(null).ShouldBeEmpty();
        TodoTool.Parse("   ").ShouldBeEmpty();

        var (_, store, convId) = NewTool();
        // Seed a corrupt payload directly, then list via the tool -> empty, no throw.
        var conv = (await store.GetAsync(convId))! with { TodoJson = "{ broken" };
        await store.SaveAsync(conv);
        var tool = new TodoTool(convId, store);
        ReadText(await ExecuteAsync(tool, new Dictionary<string, object?> { ["action"] = "list" }))
            .ShouldContain("Todo list is empty");
    }

    // ── live-update broadcast (#1464 step 5) ────────────────────────────

    [Fact]
    public async Task Write_BroadcastsTodoUpdate_WithAgentAndConversation()
    {
        var store = new InMemoryConversationStore();
        var convId = ConversationId.Create();
        await store.CreateAsync(new Conversation
        {
            ConversationId = convId,
            AgentId = AgentId.From("agent-broadcast"),
            Title = "Todo broadcast",
        });

        var notifier = new CapturingTodoNotifier();
        var tool = new TodoTool(convId, store, AgentId.From("agent-broadcast"), [notifier]);

        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "write",
            ["items"] = ItemsJson("""[{ "text": "broadcast me" }]"""),
        });

        notifier.Calls.Count.ShouldBe(1);
        notifier.Calls[0].AgentId.ShouldBe("agent-broadcast");
        notifier.Calls[0].ConversationId.ShouldBe(convId.Value);
        notifier.Calls[0].TodoJson.ShouldNotBeNull();
        notifier.Calls[0].TodoJson!.ShouldContain("broadcast me");
    }

    [Fact]
    public async Task Clear_BroadcastsNullTodoJson()
    {
        var store = new InMemoryConversationStore();
        var convId = ConversationId.Create();
        await store.CreateAsync(new Conversation
        {
            ConversationId = convId,
            AgentId = AgentId.From("agent-clear"),
            Title = "Todo clear",
        });

        var notifier = new CapturingTodoNotifier();
        var tool = new TodoTool(convId, store, AgentId.From("agent-clear"), [notifier]);
        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "write",
            ["items"] = ItemsJson("""[{ "text": "temp" }]"""),
        });
        await ExecuteAsync(tool, new Dictionary<string, object?> { ["action"] = "clear" });

        // write then clear => 2 broadcasts; the last carries null (empty list serializes to null).
        notifier.Calls.Count.ShouldBe(2);
        notifier.Calls[^1].TodoJson.ShouldBeNull();
    }

    [Fact]
    public async Task Broadcast_FailureDoesNotFailTheToolCall()
    {
        var store = new InMemoryConversationStore();
        var convId = ConversationId.Create();
        await store.CreateAsync(new Conversation
        {
            ConversationId = convId,
            AgentId = AgentId.From("agent-throw"),
            Title = "Todo throw",
        });

        var tool = new TodoTool(convId, store, AgentId.From("agent-throw"), [new ThrowingTodoNotifier()]);

        // The notifier throws, but the write must still succeed and persist.
        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "write",
            ["items"] = ItemsJson("""[{ "text": "survives broadcast failure" }]"""),
        });

        ReadText(result).ShouldContain("Todo list set with 1 item(s)");
        TodoTool.Parse((await store.GetAsync(convId))!.TodoJson).Count.ShouldBe(1);
    }

    private sealed class CapturingTodoNotifier : BotNexus.Gateway.Abstractions.Agents.IAgentTodoNotifier
    {
        public List<(string AgentId, string ConversationId, string? TodoJson)> Calls { get; } = [];

        public Task NotifyTodoUpdatedAsync(string agentId, string conversationId, string? todoJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((agentId, conversationId, todoJson));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingTodoNotifier : BotNexus.Gateway.Abstractions.Agents.IAgentTodoNotifier
    {
        public Task NotifyTodoUpdatedAsync(string agentId, string conversationId, string? todoJson, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("transport down");
    }

    private static System.Text.Json.JsonElement ItemsJson(string json)
        => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<AgentToolResult> ExecuteAsync(
        IAgentTool tool,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken = default)
    {
        var prepared = await tool.PrepareArgumentsAsync(args, cancellationToken);
        return await tool.ExecuteAsync("call-todo-test", prepared, cancellationToken);
    }

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(c => c.Type == AgentToolContentType.Text).Value;
}
