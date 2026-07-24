using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Globalization;
using Shouldly;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class WorkspacePanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();

    public WorkspacePanelTests()
    {
        _ctx.Services.AddSingleton(_restClient);
        _ctx.JSInterop.SetupVoid("BotNexus.splitter.init", _ => true);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Shows_loading_state_while_initial_request_is_pending()
    {
        var pending = new TaskCompletionSource<WorkspaceResponseDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _restClient.GetWorkspaceAsync("agent-1", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => pending.Task);

        var cut = _ctx.Render<WorkspacePanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));

        cut.Markup.ShouldContain("Loading workspace");
        pending.SetResult(new WorkspaceResponseDto("directory", "", [], null, null, null, null, null));
    }

    [Fact]
    public void Shows_empty_workspace_state()
    {
        _restClient.GetWorkspaceAsync("agent-1", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<WorkspaceResponseDto?>(new WorkspaceResponseDto("directory", "", [], null, null, null, null, null)));

        var cut = _ctx.Render<WorkspacePanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Workspace is empty."));
    }

    [Fact]
    public void First_render_initializes_splitter_with_widened_default_width_baseline()
    {
        _restClient.GetWorkspaceAsync("agent-1", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<WorkspaceResponseDto?>(new WorkspaceResponseDto("directory", "", [], null, null, null, null, null)));

        _ctx.Render<WorkspacePanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));

        var invocation = Assert.Single(_ctx.JSInterop.Invocations, i => i.Identifier == "BotNexus.splitter.init");
        Assert.Equal("workspace-panel-agent-1", Assert.IsType<string>(invocation.Arguments[0]));
        Assert.Equal("bn-workspace-tree-width-agent-1", Assert.IsType<string>(invocation.Arguments[1]));
        Assert.Equal(560, Convert.ToInt32(invocation.Arguments[2], CultureInfo.InvariantCulture));
        Assert.Equal(200, Convert.ToInt32(invocation.Arguments[3], CultureInfo.InvariantCulture));
        Assert.Equal(0.7d, Convert.ToDouble(invocation.Arguments[4], CultureInfo.InvariantCulture), 3);
        Assert.Equal(0.5d, Convert.ToDouble(invocation.Arguments[5], CultureInfo.InvariantCulture), 3);
    }

    [Fact]
    public void Selecting_file_renders_file_content()
    {
        _restClient.GetWorkspaceAsync("agent-1", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var path = callInfo.ArgAt<string?>(1);
                return path switch
                {
                    null => Task.FromResult<WorkspaceResponseDto?>(new WorkspaceResponseDto(
                        "directory",
                        "",
                        [new WorkspaceEntryDto("readme.md", "file", 22)],
                        null,
                        null,
                        null,
                        null,
                        null)),
                    "readme.md" => Task.FromResult<WorkspaceResponseDto?>(new WorkspaceResponseDto(
                        "file",
                        "readme.md",
                        null,
                        "hello workspace",
                        22,
                        "utf-8",
                        null,
                        null)),
                    _ => Task.FromResult<WorkspaceResponseDto?>(null)
                };
            });

        var cut = _ctx.Render<WorkspacePanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));
        cut.WaitForAssertion(() => cut.Find("button[data-path='readme.md']"));

        cut.Find("button[data-path='readme.md']").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("hello workspace"));
    }

    [Fact]
    public void Selecting_text_file_response_renders_file_content()
    {
        _restClient.GetWorkspaceAsync("agent-1", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var path = callInfo.ArgAt<string?>(1);
                return path switch
                {
                    null => Task.FromResult<WorkspaceResponseDto?>(new WorkspaceResponseDto(
                        Type: "directory",
                        Path: "",
                        Entries: [new WorkspaceEntryDto("readme.md", "file", 22)],
                        Content: null,
                        Size: null,
                        Encoding: null,
                        IsTruncated: null,
                        Binary: null)),
                    "readme.md" => Task.FromResult<WorkspaceResponseDto?>(new WorkspaceResponseDto(
                        Type: "text",
                        Path: "readme.md",
                        Entries: null,
                        Content: "hello workspace",
                        Size: 22,
                        Encoding: "utf-8",
                        IsTruncated: null,
                        Binary: null)),
                    _ => Task.FromResult<WorkspaceResponseDto?>(null)
                };
            });

        var cut = _ctx.Render<WorkspacePanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));
        cut.WaitForAssertion(() => cut.Find("button[data-path='readme.md']"));

        cut.Find("button[data-path='readme.md']").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("hello workspace"));
    }

    [Fact]
    public void Expanding_directory_loads_nested_entries()
    {
        _restClient.GetWorkspaceAsync("agent-1", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var path = callInfo.ArgAt<string?>(1);
                return path switch
                {
                    null => Task.FromResult<WorkspaceResponseDto?>(new WorkspaceResponseDto(
                        "directory",
                        "",
                        [new WorkspaceEntryDto("memory", "directory", null)],
                        null,
                        null,
                        null,
                        null,
                        null)),
                    "memory" => Task.FromResult<WorkspaceResponseDto?>(new WorkspaceResponseDto(
                        "directory",
                        "memory",
                        [new WorkspaceEntryDto("daily.md", "file", 100)],
                        null,
                        null,
                        null,
                        null,
                        null)),
                    _ => Task.FromResult<WorkspaceResponseDto?>(null)
                };
            });

        var cut = _ctx.Render<WorkspacePanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));
        cut.WaitForAssertion(() => cut.Find("button[data-path='memory']"));

        cut.Find("button[data-path='memory']").Click();

        cut.WaitForAssertion(() => cut.Find("button[data-path='memory/daily.md']"));
    }

    [Fact]
    public void Shows_workspace_error_when_initial_load_fails()
    {
        _restClient.GetWorkspaceAsync("agent-1", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<WorkspaceResponseDto?>(new HttpRequestException("boom")));

        var cut = _ctx.Render<WorkspacePanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Unable to load workspace."));
    }

    [Fact]
    public void Shows_file_error_when_selected_file_load_fails()
    {
        _restClient.GetWorkspaceAsync("agent-1", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var path = callInfo.ArgAt<string?>(1);
                return path switch
                {
                    null => Task.FromResult<WorkspaceResponseDto?>(new WorkspaceResponseDto(
                        "directory",
                        "",
                        [new WorkspaceEntryDto("readme.md", "file", 22)],
                        null,
                        null,
                        null,
                        null,
                        null)),
                    "readme.md" => Task.FromException<WorkspaceResponseDto?>(new HttpRequestException("boom")),
                    _ => Task.FromResult<WorkspaceResponseDto?>(null)
                };
            });

        var cut = _ctx.Render<WorkspacePanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));
        cut.WaitForAssertion(() => cut.Find("button[data-path='readme.md']"));

        cut.Find("button[data-path='readme.md']").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Unable to load file content."));
    }

    [Fact]
    public void App_css_contains_workspace_mobile_hooks()
    {
        var cssPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "wwwroot",
            "css",
            "app.css");

        var css = File.ReadAllText(cssPath);

        css.ShouldContain(".workspace-panel.mobile-tree .workspace-viewer-pane");
        css.ShouldContain(".workspace-panel.mobile-viewer .workspace-tree-pane");
        css.ShouldContain(".workspace-mobile-back");
        css.ShouldContain(".workspace-tree-row-wrapper");
        css.ShouldContain(".workspace-tree-delete");
    }

    // ── Issue #345: auto-refresh on turn-end ───────────────────────────────────

    [Fact]
    public void Refreshes_workspace_when_active_agent_turn_ends()
    {
        // Arrange: set up store with active agent that starts streaming
        var store = new ClientStateStore();
        store.UpsertAgent(new AgentState { AgentId = "agent-1", IsStreaming = true });
        store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);

        var callCount = 0;
        _restClient.GetWorkspaceAsync("agent-1", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return Task.FromResult<WorkspaceResponseDto?>(new WorkspaceResponseDto(
                    "directory", "", [], null, null, null, null, null));
            });

        var cut = _ctx.Render<WorkspacePanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.Store, store));
        cut.WaitForAssertion(() => callCount.ShouldBeGreaterThan(0));
        var loadCountAfterMount = callCount;

        // Act: agent turn ends (IsStreaming transitions false)
        store.GetAgent("agent-1")!.IsStreaming = false;
        store.NotifyChanged();

        // Assert: workspace was reloaded
        cut.WaitForAssertion(() => callCount.ShouldBeGreaterThan(loadCountAfterMount));
    }

    [Fact]
    public void Does_not_refresh_workspace_when_background_agent_turn_ends()
    {
        // Arrange: store has agent-1 as active, agent-2 is background
        var store = new ClientStateStore();
        store.UpsertAgent(new AgentState { AgentId = "agent-1", IsStreaming = false });
        store.UpsertAgent(new AgentState { AgentId = "agent-2", IsStreaming = true });
        store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);

        var callCount = 0;
        _restClient.GetWorkspaceAsync("agent-1", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return Task.FromResult<WorkspaceResponseDto?>(new WorkspaceResponseDto(
                    "directory", "", [], null, null, null, null, null));
            });

        var cut = _ctx.Render<WorkspacePanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.Store, store));
        cut.WaitForAssertion(() => callCount.ShouldBeGreaterThan(0));
        var loadCountAfterMount = callCount;

        // Act: background agent-2 turn ends
        store.GetAgent("agent-2")!.IsStreaming = false;
        store.NotifyChanged();

        // Small delay to confirm no reload fired
        cut.WaitForAssertion(() => callCount.ShouldBe(loadCountAfterMount));
    }

    [Fact]
    public void Mid_size_file_below_server_limit_shows_no_truncation_notice_and_stays_editable()
    {
        // Issue #1969: a file larger than the old 200K client cap but under the
        // 512 KiB server read limit is returned in full — it must NOT show the
        // truncation banner, and Edit must remain enabled (saving is lossless).
        var content = new string('x', 300_000);
        _restClient.GetWorkspaceAsync("agent-1", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var path = callInfo.ArgAt<string?>(1);
                return path switch
                {
                    null => Task.FromResult<WorkspaceResponseDto?>(new WorkspaceResponseDto(
                        "directory", "", [new WorkspaceEntryDto("big.md", "file", 300_000)],
                        null, null, null, null, null)),
                    "big.md" => Task.FromResult<WorkspaceResponseDto?>(new WorkspaceResponseDto(
                        "text", "big.md", null, content, 300_000, "utf-8", IsTruncated: false, Binary: null)),
                    _ => Task.FromResult<WorkspaceResponseDto?>(null)
                };
            });

        var cut = _ctx.Render<WorkspacePanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));
        cut.WaitForAssertion(() => cut.Find("button[data-path='big.md']"));
        cut.Find("button[data-path='big.md']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldNotContain("Content was truncated for safe preview");
            var edit = cut.Find("button.workspace-btn-edit");
            edit.HasAttribute("disabled").ShouldBeFalse();
        });
    }

    [Fact]
    public void Truncated_file_shows_notice_and_disables_edit()
    {
        // Issue #1969: a genuinely server-truncated file must show the banner and
        // disable Edit so saving cannot silently drop the unseen tail.
        _restClient.GetWorkspaceAsync("agent-1", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var path = callInfo.ArgAt<string?>(1);
                return path switch
                {
                    null => Task.FromResult<WorkspaceResponseDto?>(new WorkspaceResponseDto(
                        "directory", "", [new WorkspaceEntryDto("huge.md", "file", 5_000_000)],
                        null, null, null, null, null)),
                    "huge.md" => Task.FromResult<WorkspaceResponseDto?>(new WorkspaceResponseDto(
                        "text", "huge.md", null, "partial", 5_000_000, "utf-8", IsTruncated: true, Binary: null)),
                    _ => Task.FromResult<WorkspaceResponseDto?>(null)
                };
            });

        var cut = _ctx.Render<WorkspacePanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));
        cut.WaitForAssertion(() => cut.Find("button[data-path='huge.md']"));
        cut.Find("button[data-path='huge.md']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("Content was truncated for safe preview");
            cut.Find("button.workspace-btn-edit").HasAttribute("disabled").ShouldBeTrue();
        });
    }

    [Fact]
    public void App_css_contains_full_height_editor_rule()
    {
        // Issue #1969: the edit textarea had no CSS at all and collapsed to 2 rows.
        var cssPath = Path.Combine(
            FindRepositoryRoot(),
            "src", "extensions", "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "wwwroot", "css", "app.css");

        var css = File.ReadAllText(cssPath);

        css.ShouldContain(".workspace-file-editor");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate BotNexus.slnx from test base directory.");
    }
}
