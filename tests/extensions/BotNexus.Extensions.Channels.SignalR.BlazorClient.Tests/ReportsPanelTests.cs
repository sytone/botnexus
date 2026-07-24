using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Globalization;
using Shouldly;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ReportsPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();

    public ReportsPanelTests()
    {
        _ctx.Services.AddSingleton(_restClient);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Shows_loading_state_while_report_list_is_pending()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<ReportListItemDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _restClient.GetReportsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(_ => pending.Task);

        var cut = _ctx.Render<ReportsPanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));

        cut.Markup.ShouldContain("Loading reports");
        pending.SetResult([]);
    }

    [Fact]
    public void Shows_empty_state_when_reports_directory_has_no_markdown_files()
    {
        _restClient.GetReportsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReportListItemDto>>([]));

        var cut = _ctx.Render<ReportsPanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("No reports found in this workspace yet."));
    }

    [Fact]
    public void First_render_initializes_splitter_with_legacy_default_width_baseline()
    {
        _restClient.GetReportsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReportListItemDto>>([]));

        _ctx.Render<ReportsPanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));

        var invocation = Assert.Single(_ctx.JSInterop.Invocations, i => i.Identifier == "BotNexus.splitter.init");
        Assert.Equal("reports-panel-agent-1", Assert.IsType<string>(invocation.Arguments[0]));
        Assert.Equal("bn-reports-list-width-agent-1", Assert.IsType<string>(invocation.Arguments[1]));
        Assert.Equal(384, Convert.ToInt32(invocation.Arguments[2], CultureInfo.InvariantCulture));
        Assert.Equal(140, Convert.ToInt32(invocation.Arguments[3], CultureInfo.InvariantCulture));
        Assert.Equal(0.65d, Convert.ToDouble(invocation.Arguments[4], CultureInfo.InvariantCulture), 3);
        Assert.Equal(0.33d, Convert.ToDouble(invocation.Arguments[5], CultureInfo.InvariantCulture), 3);
    }

    [Fact]
    public void Selecting_report_fetches_and_renders_markdown_content()
    {
        _restClient.GetReportsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReportListItemDto>>(
                [new ReportListItemDto("weekly.md", 42, DateTimeOffset.UtcNow)]));
        _restClient.GetReportAsync("agent-1", "weekly.md", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReportContentDto?>(new ReportContentDto(
                "weekly.md",
                42,
                false,
                DateTimeOffset.UtcNow,
                "# Weekly",
                "utf-8")));
        _ctx.JSInterop.Setup<string>("BotNexus.renderMarkdown", _ => true).SetResult("<h1>Weekly</h1>");

        var cut = _ctx.Render<ReportsPanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));
        cut.WaitForAssertion(() => cut.Find("button[data-report-name='weekly.md']"));

        cut.Find("button[data-report-name='weekly.md']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("reports/weekly.md");
            cut.Markup.ShouldContain("<h1>Weekly</h1>");
        });
    }

    [Fact]
    public void Shows_error_state_when_reports_list_load_fails()
    {
        _restClient.GetReportsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<ReportListItemDto>>(new HttpRequestException("boom")));

        var cut = _ctx.Render<ReportsPanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Unable to load reports."));
    }

    [Fact]
    public void Falls_back_to_plain_text_when_markdown_renderer_is_unavailable()
    {
        _restClient.GetReportsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReportListItemDto>>(
                [new ReportListItemDto("weekly.md", 42, DateTimeOffset.UtcNow)]));
        _restClient.GetReportAsync("agent-1", "weekly.md", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReportContentDto?>(new ReportContentDto(
                "weekly.md",
                42,
                false,
                DateTimeOffset.UtcNow,
                "# Weekly",
                "utf-8")));
        _ctx.JSInterop.Setup<string>("BotNexus.renderMarkdown", _ => true)
            .SetException(new InvalidOperationException("No JS runtime"));

        var cut = _ctx.Render<ReportsPanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));
        cut.WaitForAssertion(() => cut.Find("button[data-report-name='weekly.md']"));

        cut.Find("button[data-report-name='weekly.md']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("Markdown renderer unavailable");
            cut.Markup.ShouldContain("# Weekly");
        });
    }

    [Fact]
    public void Plain_text_fallback_escapes_html_content_safely()
    {
        _restClient.GetReportsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReportListItemDto>>(
                [new ReportListItemDto("unsafe.md", 128, DateTimeOffset.UtcNow)]));
        _restClient.GetReportAsync("agent-1", "unsafe.md", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReportContentDto?>(new ReportContentDto(
                "unsafe.md",
                128,
                false,
                DateTimeOffset.UtcNow,
                "<script>alert('xss')</script>",
                "utf-8")));
        _ctx.JSInterop.Setup<string>("BotNexus.renderMarkdown", _ => true)
            .SetException(new InvalidOperationException("No JS runtime"));

        var cut = _ctx.Render<ReportsPanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));
        cut.WaitForAssertion(() => cut.Find("button[data-report-name='unsafe.md']"));

        cut.Find("button[data-report-name='unsafe.md']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("&lt;script&gt;alert('xss')&lt;/script&gt;");
            cut.Markup.ShouldNotContain("<script>alert('xss')</script>");
        });
    }

    [Fact]
    public void Mobile_back_button_returns_to_report_list_after_selecting_report()
    {
        _restClient.GetReportsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReportListItemDto>>(
                [new ReportListItemDto("weekly.md", 42, DateTimeOffset.UtcNow)]));
        _restClient.GetReportAsync("agent-1", "weekly.md", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReportContentDto?>(new ReportContentDto(
                "weekly.md",
                42,
                false,
                DateTimeOffset.UtcNow,
                "# Weekly",
                "utf-8")));
        _ctx.JSInterop.Setup<string>("BotNexus.renderMarkdown", _ => true).SetResult("<h1>Weekly</h1>");

        var cut = _ctx.Render<ReportsPanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));
        cut.WaitForAssertion(() => cut.Find("button[data-report-name='weekly.md']"));

        cut.Find("button[data-report-name='weekly.md']").Click();
        cut.WaitForAssertion(() => cut.Find(".reports-panel.mobile-viewer"));

        cut.Find(".workspace-mobile-back").Click();

        cut.WaitForAssertion(() => cut.Find(".reports-panel.mobile-list"));
    }

    [Fact]
    public void App_css_contains_reports_mobile_hooks()
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

        css.ShouldContain(".reports-panel.mobile-list .reports-viewer-pane");
        css.ShouldContain(".reports-panel.mobile-viewer .reports-list-pane");
        css.ShouldContain(".reports-list-row");
    }

    // ── Issue #345: auto-refresh on turn-end ───────────────────────────────────

    [Fact]
    public void Refreshes_reports_when_active_agent_turn_ends()
    {
        // Arrange: store with active streaming agent
        var store = new ClientStateStore();
        store.UpsertAgent(new AgentState { AgentId = "agent-1", IsStreaming = true });
        store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);

        var callCount = 0;
        _restClient.GetReportsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return Task.FromResult<IReadOnlyList<ReportListItemDto>>([]);
            });

        var cut = _ctx.Render<ReportsPanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.Store, store));
        cut.WaitForAssertion(() => callCount.ShouldBeGreaterThan(0));
        var loadCountAfterMount = callCount;

        // Act: turn ends
        store.GetAgent("agent-1")!.IsStreaming = false;
        store.NotifyChanged();

        // Assert: reports were reloaded
        cut.WaitForAssertion(() => callCount.ShouldBeGreaterThan(loadCountAfterMount));
    }

    [Fact]
    public void Does_not_refresh_reports_when_background_agent_turn_ends()
    {
        // Arrange: agent-1 is active (not streaming), agent-2 is background
        var store = new ClientStateStore();
        store.UpsertAgent(new AgentState { AgentId = "agent-1", IsStreaming = false });
        store.UpsertAgent(new AgentState { AgentId = "agent-2", IsStreaming = true });
        store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);

        var callCount = 0;
        _restClient.GetReportsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return Task.FromResult<IReadOnlyList<ReportListItemDto>>([]);
            });

        var cut = _ctx.Render<ReportsPanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.Store, store));
        cut.WaitForAssertion(() => callCount.ShouldBeGreaterThan(0));
        var loadCountAfterMount = callCount;

        // Act: background agent turn ends
        store.GetAgent("agent-2")!.IsStreaming = false;
        store.NotifyChanged();

        // Confirm no reload fired for the active panel
        cut.WaitForAssertion(() => callCount.ShouldBe(loadCountAfterMount));
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
