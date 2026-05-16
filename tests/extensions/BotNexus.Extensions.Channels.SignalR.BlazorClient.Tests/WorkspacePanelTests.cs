using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class WorkspacePanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();

    public WorkspacePanelTests()
    {
        _ctx.Services.AddSingleton(_restClient);
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
