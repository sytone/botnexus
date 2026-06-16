using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// bUnit render tests for the per-conversation Todo panel (#1464 step 5). The panel is a read-only
/// live view of the conversation's persisted <c>TodoJson</c> document.
/// </summary>
public sealed class TodoPanelTests : IDisposable
{
    private const string SampleTodoJson =
        "{\"items\":["
        + "{\"id\":\"a\",\"text\":\"Write the failing test\",\"status\":\"done\"},"
        + "{\"id\":\"b\",\"text\":\"Implement the feature\",\"status\":\"in_progress\"},"
        + "{\"id\":\"c\",\"text\":\"Update the docs\",\"status\":\"pending\"},"
        + "{\"id\":\"d\",\"text\":\"Abandoned idea\",\"status\":\"cancelled\"}"
        + "]}";

    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();

    public TodoPanelTests()
    {
        _store.SeedAgents([new AgentSummary("agent-1", "Alpha")]);
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
    }

    public void Dispose() => _ctx.Dispose();

    private ConversationState SeedConversation(string convId, string? todoJson)
    {
        var agent = _store.GetAgent("agent-1")!;
        var conv = new ConversationState
        {
            ConversationId = convId,
            Title = "Test",
            Status = "Active",
        };
        conv.TodoJson = todoJson;
        agent.Conversations[convId] = conv;
        return conv;
    }

    [Fact]
    public void Shows_empty_state_when_conversation_has_no_todo()
    {
        SeedConversation("conv-1", todoJson: null);

        var cut = _ctx.Render<TodoPanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.ConversationId, "conv-1"));

        cut.Markup.ShouldContain("No todo items yet");
        cut.FindAll("[data-testid='todo-item']").ShouldBeEmpty();
    }

    [Fact]
    public void Shows_empty_state_when_conversation_id_is_null()
    {
        var cut = _ctx.Render<TodoPanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1"));

        cut.Find("[data-testid='todo-empty-state']");
    }

    [Fact]
    public void Renders_each_todo_item_with_text_and_status()
    {
        SeedConversation("conv-2", SampleTodoJson);

        var cut = _ctx.Render<TodoPanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.ConversationId, "conv-2"));

        var items = cut.FindAll("[data-testid='todo-item']");
        items.Count.ShouldBe(4);

        cut.Markup.ShouldContain("Write the failing test");
        cut.Markup.ShouldContain("Implement the feature");
        cut.Markup.ShouldContain("Update the docs");

        // Status is surfaced as a data attribute (drives badge styling).
        items[0].GetAttribute("data-status").ShouldBe("done");
        items[1].GetAttribute("data-status").ShouldBe("in_progress");
        items[2].GetAttribute("data-status").ShouldBe("pending");
        items[3].GetAttribute("data-status").ShouldBe("cancelled");
    }

    [Fact]
    public void Renders_done_progress_count()
    {
        SeedConversation("conv-3", SampleTodoJson);

        var cut = _ctx.Render<TodoPanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.ConversationId, "conv-3"));

        // One item is done out of four.
        var progress = cut.Find("[data-testid='todo-progress']");
        progress.TextContent.ShouldContain("1 / 4");
    }

    [Fact]
    public void Malformed_todo_json_renders_empty_state_without_throwing()
    {
        SeedConversation("conv-4", "{ this is not valid json");

        var cut = _ctx.Render<TodoPanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.ConversationId, "conv-4"));

        cut.Find("[data-testid='todo-empty-state']");
        cut.FindAll("[data-testid='todo-item']").ShouldBeEmpty();
    }

    [Fact]
    public void Live_store_update_re_renders_the_panel_without_reload()
    {
        var conv = SeedConversation("conv-5", todoJson: null);

        var cut = _ctx.Render<TodoPanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.ConversationId, "conv-5"));

        cut.Markup.ShouldContain("No todo items yet");

        // Simulate a TodoUpdated event applying new state then notifying the store.
        conv.TodoJson = "{\"items\":[{\"id\":\"x\",\"text\":\"Newly added task\",\"status\":\"pending\"}]}";
        _store.NotifyChanged();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("Newly added task");
            cut.FindAll("[data-testid='todo-item']").Count.ShouldBe(1);
        });
    }

    [Fact]
    public void Items_with_blank_text_are_skipped()
    {
        SeedConversation("conv-6",
            "{\"items\":[{\"id\":\"a\",\"text\":\"Real task\",\"status\":\"pending\"},{\"id\":\"b\",\"text\":\"\",\"status\":\"pending\"}]}");

        var cut = _ctx.Render<TodoPanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.ConversationId, "conv-6"));

        cut.FindAll("[data-testid='todo-item']").Count.ShouldBe(1);
        cut.Markup.ShouldContain("Real task");
    }

    // ── Close-out gate (#1464 step 6 / #1470) ────────────────────────────────
    // Warn-only signal: when a run has ended (the conversation is idle, i.e.
    // IsTurnActive == false) and the todo plan still has in_progress items, the
    // panel surfaces a non-fatal indicator that the run ended mid-plan. Composes
    // with the merged RunStarted/RunEnded bracket (#1458) which drives IsRunActive.

    private const string InProgressTodoJson =
        "{\"items\":["
        + "{\"id\":\"a\",\"text\":\"Finished step\",\"status\":\"done\"},"
        + "{\"id\":\"b\",\"text\":\"Still working\",\"status\":\"in_progress\"}"
        + "]}";

    private const string AllDoneTodoJson =
        "{\"items\":["
        + "{\"id\":\"a\",\"text\":\"Finished step\",\"status\":\"done\"},"
        + "{\"id\":\"b\",\"text\":\"Also finished\",\"status\":\"done\"},"
        + "{\"id\":\"c\",\"text\":\"Dropped\",\"status\":\"cancelled\"}"
        + "]}";

    [Fact]
    public void Closeout_warning_shown_when_run_ended_with_in_progress_items()
    {
        var conv = SeedConversation("conv-co1", InProgressTodoJson);
        // Run has fully ended (RunEnded -> IsRunActive false; no streaming/tools).
        conv.StreamState.IsRunActive = false;
        conv.StreamState.IsStreaming = false;

        var cut = _ctx.Render<TodoPanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.ConversationId, "conv-co1"));

        var warning = cut.Find("[data-testid='todo-closeout-warning']");
        warning.TextContent.ShouldContain("1");
        warning.TextContent.ToLowerInvariant().ShouldContain("in progress");
    }

    [Fact]
    public void Closeout_warning_hidden_when_run_ended_and_all_items_resolved()
    {
        var conv = SeedConversation("conv-co2", AllDoneTodoJson);
        conv.StreamState.IsRunActive = false;
        conv.StreamState.IsStreaming = false;

        var cut = _ctx.Render<TodoPanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.ConversationId, "conv-co2"));

        cut.FindAll("[data-testid='todo-closeout-warning']").ShouldBeEmpty();
    }

    [Fact]
    public void Closeout_warning_hidden_when_todo_list_is_empty()
    {
        var conv = SeedConversation("conv-co3", todoJson: null);
        conv.StreamState.IsRunActive = false;

        var cut = _ctx.Render<TodoPanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.ConversationId, "conv-co3"));

        cut.FindAll("[data-testid='todo-closeout-warning']").ShouldBeEmpty();
    }

    [Fact]
    public void Closeout_warning_hidden_while_run_is_still_active()
    {
        // In-progress items are expected DURING a run — only warn once the run ends.
        var conv = SeedConversation("conv-co4", InProgressTodoJson);
        conv.StreamState.IsRunActive = true;

        var cut = _ctx.Render<TodoPanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.ConversationId, "conv-co4"));

        cut.FindAll("[data-testid='todo-closeout-warning']").ShouldBeEmpty();
    }

    [Fact]
    public void Closeout_warning_appears_live_when_run_ends_with_in_progress_items()
    {
        var conv = SeedConversation("conv-co5", InProgressTodoJson);
        conv.StreamState.IsRunActive = true; // run in flight, no warning yet

        var cut = _ctx.Render<TodoPanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.ConversationId, "conv-co5"));

        cut.FindAll("[data-testid='todo-closeout-warning']").ShouldBeEmpty();

        // RunEnded arrives: run state clears and the store notifies.
        conv.StreamState.IsRunActive = false;
        _store.NotifyChanged();

        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='todo-closeout-warning']"));
    }
}
