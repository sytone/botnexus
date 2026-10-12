using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>Turn-end refresh must update data without resetting the user's workspace view (#4786).</summary>
public sealed class WorkspacePanelRefreshTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();
    private readonly ClientStateStore _store = new();
    private readonly AgentState _agent = new() { AgentId = "agent-1", IsStreaming = true };
    private readonly List<TaskCompletionSource<WorkspaceResponseDto?>> _pending = [];

    public WorkspacePanelRefreshTests()
    {
        _ctx.Services.AddSingleton(_restClient);
        _ctx.JSInterop.SetupVoid("BotNexus.splitter.init", _ => true).SetVoidResult();
        _store.UpsertAgent(_agent);
    }

    public void Dispose()
    {
        // Also release barriers when a preservation assertion fails before the response is sent.
        foreach (var pending in _pending)
            pending.TrySetResult(null);
        _ctx.Dispose();
    }

    [Fact]
    public async Task Turn_end_preserves_nested_expansions_and_refreshes_each_visible_directory()
    {
        var refreshed = false;
        Stub(path => Task.FromResult<WorkspaceResponseDto?>(path switch
        {
            "" => Directory("", Folder("memory"), File(refreshed ? "new-root.md" : "old-root.md")),
            "memory" => Directory("memory", Folder("days"), File(refreshed ? "new-parent.md" : "old-parent.md")),
            "memory/days" => Directory("memory/days", File(refreshed ? "new-day.md" : "old-day.md")),
            _ => null
        }));
        var cut = Render();
        cut.Find(Row("memory")).Click();
        cut.Find(Row("memory/days")).Click();
        cut.Find(Row("memory/days/old-day.md"));

        refreshed = true;
        await EndTurnAsync(cut);

        cut.WaitForAssertion(() =>
        {
            AssertExpanded(cut, "memory", true);
            AssertExpanded(cut, "memory/days", true);
            cut.Find(Row("new-root.md"));
            cut.Find(Row("memory/new-parent.md"));
            cut.Find(Row("memory/days/new-day.md"));
            cut.FindAll(Row("old-root.md")).ShouldBeEmpty();
            cut.FindAll(Row("memory/old-parent.md")).ShouldBeEmpty();
            cut.FindAll(Row("memory/days/old-day.md")).ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Pending_root_refresh_keeps_rows_interactive_and_respects_collapse_and_new_expand()
    {
        var root = PendingResponse();
        var started = Signal();
        var rootCalls = 0;
        Stub(path => path switch
        {
            "" when ++rootCalls > 1 => Hold(root, started),
            "" => Task.FromResult<WorkspaceResponseDto?>(Directory("", Folder("memory"), Folder("notes"))),
            "memory" => Task.FromResult<WorkspaceResponseDto?>(Directory("memory", File("daily.md"))),
            "notes" => Task.FromResult<WorkspaceResponseDto?>(Directory("notes", File("note.md"))),
            _ => Task.FromResult<WorkspaceResponseDto?>(null)
        });
        var cut = Render();
        cut.Find(Row("memory")).Click();
        await EndTurnAsync(cut);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Force a render while the response is held; don't rely on a stale pre-refresh DOM.
        cut.Render();
        AssertExpanded(cut, "memory", true);
        cut.Find(Row("memory/daily.md")).HasAttribute("disabled").ShouldBeFalse();
        cut.Find(Row("memory")).Click();
        cut.Find(Row("notes")).Click();
        cut.Find(Row("notes/note.md"));

        root.SetResult(Directory("", Folder("memory"), Folder("notes"), File("updated.md")));
        cut.WaitForAssertion(() =>
        {
            cut.Find(Row("updated.md"));
            AssertExpanded(cut, "memory", false);
            AssertExpanded(cut, "notes", true);
            cut.FindAll(Row("memory/daily.md")).ShouldBeEmpty();
            cut.Find(Row("notes/note.md"));
        });
    }

    [Fact]
    public async Task Pending_directory_refresh_keeps_cached_children_interactive_and_does_not_reexpand_collapsed_folder()
    {
        var directory = PendingResponse();
        var started = Signal();
        var refresh = false;
        Stub(path => path switch
        {
            "" => Task.FromResult<WorkspaceResponseDto?>(Directory("", Folder("memory"))),
            "memory" when refresh => Hold(directory, started),
            "memory" => Task.FromResult<WorkspaceResponseDto?>(Directory("memory", File("daily.md"))),
            "memory/daily.md" => Task.FromResult<WorkspaceResponseDto?>(Text("memory/daily.md", "saved content")),
            _ => Task.FromResult<WorkspaceResponseDto?>(null)
        });
        var cut = Render();
        cut.Find(Row("memory")).Click();
        refresh = true;
        await EndTurnAsync(cut);
        cut.Render();
        // Assert preservation before waiting for a directory request: the old code fails
        // for clearing expansion, not for failing to start a request within a deadline.
        AssertExpanded(cut, "memory", true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cut.Render();
        cut.Find(Row("memory/daily.md")).Click();
        cut.Find("pre.workspace-file-content").TextContent.ShouldBe("saved content");
        cut.Find(Row("memory")).Click();
        directory.SetResult(Directory("memory", File("updated.md")));
        await cut.InvokeAsync(() => { });
        cut.WaitForAssertion(() => AssertExpanded(cut, "memory", false));
        cut.Find(Row("memory")).Click();
        cut.WaitForAssertion(() => cut.Find(Row("memory/updated.md")));
    }

    [Fact]
    public async Task Turn_end_preserves_selected_file_and_unsaved_editor_draft_without_rereading_file()
    {
        var root = PendingResponse();
        var started = Signal();
        var refresh = false;
        Stub(path => path switch
        {
            "" when refresh => Hold(root, started),
            "" => Task.FromResult<WorkspaceResponseDto?>(Directory("", File("readme.md"))),
            "readme.md" => Task.FromResult<WorkspaceResponseDto?>(Text("readme.md", "saved content")),
            _ => Task.FromResult<WorkspaceResponseDto?>(null)
        });
        var cut = Render();
        cut.Find(Row("readme.md")).Click();
        cut.Find("button.workspace-btn-edit").Click();
        cut.Find("textarea.workspace-file-editor").Input("unsaved draft");
        refresh = true;
        await EndTurnAsync(cut);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cut.Render();
        AssertDraft(cut);
        root.SetResult(Directory("", File("readme.md"), File("updated.md")));
        cut.WaitForAssertion(() =>
        {
            cut.Find(Row("updated.md"));
            cut.Find(Row("readme.md")).ClassList.ShouldContain("selected");
            AssertDraft(cut);
        });
        await _restClient.Received(1).GetWorkspaceAsync("agent-1", "readme.md", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Repeated_turn_ends_serialize_refresh_and_coalesce_pending_work_into_one_followup()
    {
        var first = PendingResponse();
        var second = PendingResponse();
        var firstStarted = Signal();
        var secondStarted = Signal();
        var calls = 0;
        Stub(_ => ++calls switch
        {
            1 => Task.FromResult<WorkspaceResponseDto?>(Directory("", File("initial.md"))),
            2 => Hold(first, firstStarted),
            _ => Hold(second, secondStarted)
        });
        var cut = Render();
        await EndTurnAsync(cut);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await StartAndEndTurnAsync(cut);
        await StartAndEndTurnAsync(cut);
        // Every notification ran on the dispatcher; the first response is still held.
        // No wall-clock delay is needed to prove that overlapping requests are forbidden.
        calls.ShouldBe(2, "turn ends during a refresh must not start overlapping root loads");
        first.SetResult(Directory("", File("first.md")));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        calls.ShouldBe(3, "multiple queued turn ends should produce one followup refresh");
        second.SetResult(Directory("", File("latest.md")));
        cut.WaitForAssertion(() => cut.Find(Row("latest.md")));
        await cut.InvokeAsync(() => { });
        calls.ShouldBe(3);
        cut.FindAll(Row("first.md")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Failed_root_refresh_preserves_last_good_tree_and_retries_on_next_turn()
    {
        var calls = 0;
        Stub(path => Task.FromResult<WorkspaceResponseDto?>(path switch
        {
            "" when ++calls == 2 => null,
            "" => Directory("", Folder("memory"), File(calls > 2 ? "recovered.md" : "initial.md")),
            "memory" => Directory("memory", File("daily.md")),
            _ => null
        }));
        var cut = Render();
        cut.Find(Row("memory")).Click();
        await EndTurnAsync(cut);
        cut.Render();
        calls.ShouldBe(2);
        AssertExpanded(cut, "memory", true);
        cut.Find(Row("initial.md"));
        cut.Find(Row("memory/daily.md"));
        await StartAndEndTurnAsync(cut);
        cut.WaitForAssertion(() =>
        {
            cut.Find(Row("recovered.md"));
            AssertExpanded(cut, "memory", true);
            cut.Find(Row("memory/daily.md"));
        });
    }

    [Fact]
    public async Task Failed_directory_refresh_preserves_children_and_retries_on_next_turn()
    {
        var directoryCalls = 0;
        Stub(path => path switch
        {
            "" => Task.FromResult<WorkspaceResponseDto?>(Directory("", Folder("memory"))),
            "memory" when ++directoryCalls == 2 => Task.FromException<WorkspaceResponseDto?>(new HttpRequestException("refresh failed")),
            "memory" => Task.FromResult<WorkspaceResponseDto?>(Directory("memory", File(directoryCalls > 2 ? "recovered.md" : "daily.md"))),
            _ => Task.FromResult<WorkspaceResponseDto?>(null)
        });
        var cut = Render();
        cut.Find(Row("memory")).Click();
        await EndTurnAsync(cut);
        cut.Render();
        AssertExpanded(cut, "memory", true);
        cut.Find(Row("memory/daily.md"));
        directoryCalls.ShouldBe(2);
        await StartAndEndTurnAsync(cut);
        cut.WaitForAssertion(() =>
        {
            AssertExpanded(cut, "memory", true);
            cut.Find(Row("memory/recovered.md"));
            cut.FindAll(Row("memory/daily.md")).ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Turn_end_replaces_preexisting_expansion_request_with_fresh_listing()
    {
        var oldResponse = PendingResponse();
        var oldStarted = Signal();
        var calls = 0;
        Stub(path => path switch
        {
            "" => Task.FromResult<WorkspaceResponseDto?>(Directory("", Folder("memory"))),
            "memory" when ++calls == 1 => Hold(oldResponse, oldStarted),
            "memory" => Task.FromResult<WorkspaceResponseDto?>(Directory("memory", File("fresh.md"))),
            _ => Task.FromResult<WorkspaceResponseDto?>(null)
        });
        var cut = Render();
        var expansion = cut.Find(Row("memory")).ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await EndTurnAsync(cut);
        oldResponse.SetResult(Directory("memory", File("stale.md")));
        await expansion;
        cut.WaitForAssertion(() =>
        {
            AssertExpanded(cut, "memory", true);
            cut.Find(Row("memory/fresh.md"));
            cut.FindAll(Row("memory/stale.md")).ShouldBeEmpty();
        });
        calls.ShouldBe(2);
    }

    [Fact]
    public async Task Nested_selected_file_remains_highlighted_after_turn_end()
    {
        Stub(path => Task.FromResult<WorkspaceResponseDto?>(path switch
        {
            "" => Directory("", Folder("memory")),
            "memory" => Directory("memory", File("daily.md")),
            "memory/daily.md" => Text("memory/daily.md", "selected content"),
            _ => null
        }));
        var cut = Render();
        cut.Find(Row("memory")).Click();
        cut.Find(Row("memory/daily.md")).Click();
        await EndTurnAsync(cut);
        cut.WaitForAssertion(() =>
        {
            cut.Find(Row("memory/daily.md")).ClassList.ShouldContain("selected");
            cut.Find("pre.workspace-file-content").TextContent.ShouldBe("selected content");
        });
    }

    [Fact]
    public async Task Dispose_cancels_pending_refresh_and_unsubscribes_from_turn_notifications()
    {
        var pending = PendingResponse();
        var started = Signal();
        var cancelled = Signal();
        var calls = 0;
        CancellationToken requestToken = default;
        _restClient.GetWorkspaceAsync("agent-1", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls++;
                if (calls == 1)
                    return Task.FromResult<WorkspaceResponseDto?>(Directory("", File("initial.md")));
                requestToken = call.ArgAt<CancellationToken>(2);
                return Hold(pending, started);
            });
        var cut = Render();
        await EndTurnAsync(cut);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var registration = requestToken.Register(() => cancelled.TrySetResult(true));
        // Disposing the rendered wrapper only removes test observers; dispose the
        // actual component tree through bUnit's renderer to exercise IDisposable.
        await _ctx.DisposeComponentsAsync();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        requestToken.IsCancellationRequested.ShouldBeTrue();
        // Simulate an uncooperative transport completing after disposal, then deliver
        // another full turn on the renderer to check the event subscription was removed.
        pending.SetResult(Directory("", File("late.md")));
        await _ctx.Renderer.Dispatcher.InvokeAsync(() =>
        {
            _agent.IsStreaming = true;
            _store.NotifyChanged();
            _agent.IsStreaming = false;
            _store.NotifyChanged();
        });
        calls.ShouldBe(2);
        _ctx.Renderer.UnhandledException.IsCompleted.ShouldBeFalse();
    }

    private IRenderedComponent<WorkspacePanel> Render()
    {
        var cut = _ctx.Render<WorkspacePanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1").Add(x => x.Store, _store));
        cut.WaitForAssertion(() => cut.Find("button.workspace-tree-row"));
        return cut;
    }

    private void Stub(Func<string, Task<WorkspaceResponseDto?>> response) =>
        _restClient.GetWorkspaceAsync("agent-1", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => response(call.ArgAt<string?>(1) ?? string.Empty));

    private Task EndTurnAsync(IRenderedComponent<WorkspacePanel> cut) => cut.InvokeAsync(() =>
    {
        _agent.IsStreaming = false;
        _store.NotifyChanged();
    });

    private async Task StartAndEndTurnAsync(IRenderedComponent<WorkspacePanel> cut)
    {
        await cut.InvokeAsync(() =>
        {
            _agent.IsStreaming = true;
            _store.NotifyChanged();
        });
        await EndTurnAsync(cut);
    }

    private TaskCompletionSource<WorkspaceResponseDto?> PendingResponse()
    {
        var pending = new TaskCompletionSource<WorkspaceResponseDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.Add(pending);
        return pending;
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task<WorkspaceResponseDto?> Hold(TaskCompletionSource<WorkspaceResponseDto?> response, TaskCompletionSource<bool> started)
    {
        started.TrySetResult(true);
        return response.Task;
    }

    private static string Row(string path) => $"button.workspace-tree-row[data-path='{path}']";

    private static void AssertExpanded(IRenderedComponent<WorkspacePanel> cut, string path, bool expanded) =>
        cut.Find(Row(path)).QuerySelector(".workspace-tree-chevron")?.TextContent.ShouldBe(expanded ? "▾" : "▸");

    private static void AssertDraft(IRenderedComponent<WorkspacePanel> cut)
    {
        cut.Find(".workspace-file-header h3").TextContent.ShouldBe("readme.md");
        cut.Find("textarea.workspace-file-editor").GetAttribute("value").ShouldBe("unsaved draft");
        cut.Find("button.workspace-btn-save").HasAttribute("disabled").ShouldBeFalse();
    }

    private static WorkspaceEntryDto Folder(string name) => new(name, "directory", null);
    private static WorkspaceEntryDto File(string name) => new(name, "file", 10);
    private static WorkspaceResponseDto Directory(string path, params WorkspaceEntryDto[] entries) =>
        new("directory", path, entries, null, null, null, null, null);
    private static WorkspaceResponseDto Text(string path, string content) =>
        new("text", path, null, content, content.Length, "utf-8", false, false);
}
