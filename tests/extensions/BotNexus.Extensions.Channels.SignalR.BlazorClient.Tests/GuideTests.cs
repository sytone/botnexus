using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Bunit.TestDoubles;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class GuideTests : IDisposable
{
    private const string IndexJson = """
        {
          "sections": [
            {
              "id": "alpha",
              "file": "alpha.md",
              "title": "Alpha",
              "children": [
                { "id": "alpha-child", "file": "alpha-child.md", "title": "Alpha child" }
              ]
            },
            { "id": "beta", "file": "beta.md", "title": "Beta" },
            { "id": "gamma", "file": "gamma.md", "title": "Gamma" }
          ]
        }
        """;

    private readonly BunitContext _ctx = new();
    private readonly ControlledGuideHandler _http = new();
    private readonly ControlledMarkdownRuntime _js = new();

    public GuideTests()
    {
        _http.SetBody("/guide/guide-index.json", IndexJson, "application/json");
        _http.SetBody("/guide/alpha.md", "# Alpha");
        _http.SetBody("/guide/alpha-child.md", "# Alpha child");
        _http.SetBody("/guide/beta.md", "# Beta");
        _http.SetBody("/guide/gamma.md", "# Gamma");
        _js.SetRendered("# Alpha", "<h1>Alpha</h1>");
        _js.SetRendered("# Alpha child", "<h1>Alpha child</h1>");
        _js.SetRendered("# Beta", "<h1>Beta</h1>");
        _js.SetRendered("# Gamma", "<h1>Gamma</h1>");

        _ctx.Services.AddSingleton(new HttpClient(_http) { BaseAddress = new Uri("http://localhost/") });
        _ctx.Services.AddSingleton<IJSRuntime>(_js);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task Full_text_indexing_remains_in_progress_until_every_body_completes()
    {
        var cut = RenderGuide();
        var betaResponse = _http.HoldNext("/guide/beta.md");

        var search = cut.Find("[data-testid='guide-search']").InputAsync(new() { Value = "beta body" });
        await _http.WaitForRequestAsync("/guide/beta.md", 1);

        cut.Find(".guide-nav").TextContent.ShouldContain("Still indexing page contents");
        _http.RequestCount("/guide/alpha.md").ShouldBe(1);

        betaResponse.SetResult(Response("beta body"));
        await search;

        cut.Find("[data-testid='guide-nav-beta']").TextContent.ShouldContain("Beta");
        cut.Find(".guide-nav").TextContent.ShouldNotContain("Still indexing page contents");
        _http.RequestCount("/guide/alpha.md").ShouldBe(1);
        _http.RequestCount("/guide/beta.md").ShouldBe(1);
        _http.RequestCount("/guide/gamma.md").ShouldBe(1);
    }

    [Fact]
    public async Task Failed_body_is_retried_without_refetching_successful_pages()
    {
        var cut = RenderGuide();
        _http.HoldNext("/guide/beta.md").SetResult(Response("temporary failure", HttpStatusCode.ServiceUnavailable));

        await cut.Find("[data-testid='guide-search']").InputAsync(new() { Value = "recovered answer" });

        cut.Find(".guide-nav").TextContent.ShouldContain("Search again to retry");
        _http.SetBody("/guide/beta.md", "recovered answer");

        await cut.Find("[data-testid='guide-search']").InputAsync(new() { Value = "recovered answer" });

        cut.Find("[data-testid='guide-nav-beta']").TextContent.ShouldContain("Beta");
        _http.RequestCount("/guide/alpha.md").ShouldBe(1);
        _http.RequestCount("/guide/beta.md").ShouldBe(2);
        _http.RequestCount("/guide/gamma.md").ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_searches_share_one_indexing_pass()
    {
        var cut = RenderGuide();
        var betaResponse = _http.HoldNext("/guide/beta.md");
        _http.SetBody("/guide/gamma.md", "gamma body");

        var first = cut.Find("[data-testid='guide-search']").InputAsync(new() { Value = "beta body" });
        await _http.WaitForRequestAsync("/guide/beta.md", 1);
        var second = cut.Find("[data-testid='guide-search']").InputAsync(new() { Value = "gamma body" });

        _http.RequestCount("/guide/beta.md").ShouldBe(1);
        betaResponse.SetResult(Response("beta body"));
        await Task.WhenAll(first, second);

        _http.RequestCount("/guide/beta.md").ShouldBe(1);
        _http.RequestCount("/guide/gamma.md").ShouldBe(1);
    }

    [Fact]
    public async Task Disposing_during_indexing_cancels_the_active_pass()
    {
        var cut = RenderGuide();
        _ = _http.HoldNext("/guide/beta.md");

        var search = cut.Find("[data-testid='guide-search']").InputAsync(new() { Value = "beta body" });
        await _http.WaitForRequestAsync("/guide/beta.md", 1);
        cut.Instance.Dispose();
        cut.Dispose();

        await _http.WaitForCancellationAsync("/guide/beta.md");
        await search;
    }

    [Fact]
    public async Task Older_uncached_request_cannot_replace_newer_cached_selection()
    {
        var cut = RenderGuide();
        var staleResponse = _http.HoldNext("/guide/beta.md");

        var staleSelection = cut.Find("[data-testid='guide-nav-beta']").ClickAsync(new());
        await _http.WaitForRequestAsync("/guide/beta.md");

        await cut.Find("[data-testid='guide-nav-alpha']").ClickAsync(new());
        cut.Find("[data-testid='guide-nav-alpha']").ClassList.ShouldContain("active");
        cut.Find("[data-testid='guide-content']").InnerHtml.ShouldContain("Alpha");

        staleResponse.SetResult(Response("# Beta"));
        await staleSelection;

        cut.Find("[data-testid='guide-nav-alpha']").ClassList.ShouldContain("active");
        cut.Find("[data-testid='guide-content']").InnerHtml.ShouldContain("Alpha");
        cut.Find("[data-testid='guide-content']").InnerHtml.ShouldNotContain("Beta");
    }

    [Fact]
    public async Task Older_failure_cannot_replace_current_loading_or_error_state()
    {
        var cut = RenderGuide();
        var staleResponse = _http.HoldNext("/guide/beta.md");
        var currentResponse = _http.HoldNext("/guide/gamma.md");

        var staleSelection = cut.Find("[data-testid='guide-nav-beta']").ClickAsync(new());
        await _http.WaitForRequestAsync("/guide/beta.md");
        var currentSelection = cut.Find("[data-testid='guide-nav-gamma']").ClickAsync(new());
        await _http.WaitForRequestAsync("/guide/gamma.md");

        staleResponse.SetResult(Response("old request failed", HttpStatusCode.InternalServerError));
        await staleSelection;

        cut.Find("[data-testid='guide-nav-gamma']").ClassList.ShouldContain("active");
        cut.Find("[data-testid='guide-content']").TextContent.ShouldContain("Loading");
        cut.Find("[data-testid='guide-content']").TextContent.ShouldNotContain("old request failed");

        currentResponse.SetResult(Response("# Gamma"));
        await currentSelection;
        cut.Find("[data-testid='guide-content']").InnerHtml.ShouldContain("Gamma");
    }

    [Fact]
    public async Task Older_render_completion_cannot_replace_newer_article()
    {
        var cut = RenderGuide();
        var staleRender = _js.HoldNext("# Beta");

        var staleSelection = cut.Find("[data-testid='guide-nav-beta']").ClickAsync(new());
        await _js.WaitForInvocationAsync("# Beta");
        await cut.Find("[data-testid='guide-nav-gamma']").ClickAsync(new());

        cut.Find("[data-testid='guide-content']").InnerHtml.ShouldContain("Gamma");
        staleRender.SetResult("<h1>Beta</h1>");
        await staleSelection;

        cut.Find("[data-testid='guide-nav-gamma']").ClassList.ShouldContain("active");
        cut.Find("[data-testid='guide-content']").InnerHtml.ShouldContain("Gamma");
        cut.Find("[data-testid='guide-content']").InnerHtml.ShouldNotContain("Beta");
    }

    [Fact]
    public void Route_parameter_selects_requested_page_through_sanitized_renderer()
    {
        var cut = _ctx.Render<Guide>(parameters => parameters.Add(component => component.SectionId, "beta"));

        cut.Find("[data-testid='guide-nav-beta']").ClassList.ShouldContain("active");
        cut.Find("[data-testid='guide-content']").InnerHtml.ShouldContain("<h1>Beta</h1>");
        _js.Invocations.ShouldContain("# Beta");
    }

    [Fact]
    public async Task Contents_selection_updates_route_for_top_level_and_child_sections()
    {
        var cut = _ctx.Render<Guide>(parameters => parameters.Add(component => component.SectionId, "alpha"));
        var nav = _ctx.Services.GetRequiredService<NavigationManager>() as BunitNavigationManager;

        await cut.Find("[data-testid='guide-nav-beta']").ClickAsync(new());

        nav.ShouldNotBeNull();
        nav.Uri.ShouldBe("http://localhost/guide/beta");
        cut.Find("[data-testid='guide-nav-beta']").ClassList.ShouldContain("active");
        cut.Find("[data-testid='guide-content']").InnerHtml.ShouldContain("<h1>Beta</h1>");

        await cut.Find("[data-testid='guide-nav-alpha']").ClickAsync(new());
        await cut.Find("[data-testid='guide-nav-alpha-child']").ClickAsync(new());

        nav.Uri.ShouldBe("http://localhost/guide/alpha-child");
        cut.Find("[data-testid='guide-nav-alpha-child']").ClassList.ShouldContain("active");
        cut.Find("[data-testid='guide-content']").InnerHtml.ShouldContain("<h1>Alpha child</h1>");
        nav.History.Count.ShouldBe(3);
        nav.History.Select(entry => entry.Options.ReplaceHistoryEntry).ShouldAllBe(replace => !replace);
    }

    [Fact]
    public void Route_changes_restore_the_requested_section_without_adding_history()
    {
        var cut = _ctx.Render<Guide>(parameters => parameters.Add(component => component.SectionId, "alpha"));
        var nav = _ctx.Services.GetRequiredService<NavigationManager>() as BunitNavigationManager;

        cut.Render(parameters => parameters.Add(component => component.SectionId, "beta"));

        nav.ShouldNotBeNull();
        nav.History.ShouldBeEmpty();
        cut.Find("[data-testid='guide-nav-beta']").ClassList.ShouldContain("active");
        cut.Find("[data-testid='guide-content']").InnerHtml.ShouldContain("<h1>Beta</h1>");
    }

    [Fact]
    public void Invalid_route_falls_back_to_the_first_section_and_replaces_the_broken_url()
    {
        var cut = _ctx.Render<Guide>(parameters => parameters.Add(component => component.SectionId, "missing"));
        var nav = _ctx.Services.GetRequiredService<NavigationManager>() as BunitNavigationManager;

        nav.ShouldNotBeNull();
        nav.Uri.ShouldBe("http://localhost/guide/alpha");
        cut.Find("[data-testid='guide-nav-alpha']").ClassList.ShouldContain("active");
        cut.Find("[data-testid='guide-content']").InnerHtml.ShouldContain("<h1>Alpha</h1>");
        nav.History.ShouldHaveSingleItem().Options.ReplaceHistoryEntry.ShouldBeTrue();
    }

    private IRenderedComponent<Guide> RenderGuide()
    {
        var cut = _ctx.Render<Guide>();
        cut.Find("[data-testid='guide-content']").InnerHtml.ShouldContain("Alpha");
        return cut;
    }

    private static HttpResponseMessage Response(
        string body,
        HttpStatusCode status = HttpStatusCode.OK,
        string mediaType = "text/plain") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private sealed class ControlledGuideHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, Func<HttpResponseMessage>> _responses = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<HttpResponseMessage>> _held = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _requested = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _cancelled = new(StringComparer.Ordinal);

        public void SetBody(string path, string body, string mediaType = "text/plain") =>
            _responses[path] = () => Response(body, mediaType: mediaType);

        public TaskCompletionSource<HttpResponseMessage> HoldNext(string path)
        {
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _held[path] = response;
            return response;
        }

        public int RequestCount(string path) => _counts.GetValueOrDefault(path);

        public Task WaitForRequestAsync(string path) => WaitForRequestAsync(path, 1);

        public async Task WaitForRequestAsync(string path, int expectedCount)
        {
            while (RequestCount(path) < expectedCount)
            {
                await _requested.GetOrAdd(path, _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                    .Task.WaitAsync(TimeSpan.FromSeconds(30));
                _requested.TryRemove(path, out _);
            }
        }

        public Task WaitForCancellationAsync(string path) =>
            _cancelled.GetOrAdd(path, _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task.WaitAsync(TimeSpan.FromSeconds(30));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            _counts.AddOrUpdate(path, 1, (_, count) => count + 1);
            _requested.GetOrAdd(path, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

            if (_held.TryRemove(path, out var held))
            {
                using var registration = cancellationToken.Register(() =>
                    _cancelled.GetOrAdd(path, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult());
                return await held.Task.WaitAsync(cancellationToken);
            }

            return _responses.TryGetValue(path, out var response)
                ? response()
                : Response("Not found", HttpStatusCode.NotFound);
        }
    }

    private sealed class ControlledMarkdownRuntime : IJSRuntime
    {
        private readonly ConcurrentDictionary<string, string> _results = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _held = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _invoked = new(StringComparer.Ordinal);

        public ConcurrentBag<string> Invocations { get; } = [];

        public void SetRendered(string markdown, string html) => _results[markdown] = html;

        public TaskCompletionSource<string> HoldNext(string markdown)
        {
            var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _held[markdown] = result;
            return result;
        }

        public Task WaitForInvocationAsync(string markdown) =>
            _invoked.GetOrAdd(markdown, _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task.WaitAsync(TimeSpan.FromSeconds(30));

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            new(RenderAsync<TValue>(identifier, args, cancellationToken));

        private async Task<TValue> RenderAsync<TValue>(
            string identifier,
            object?[]? args,
            CancellationToken cancellationToken)
        {
            identifier.ShouldBe("BotNexus.renderMarkdown");
            var markdown = args?.ShouldHaveSingleItem()?.ToString() ?? string.Empty;
            Invocations.Add(markdown);
            _invoked.GetOrAdd(markdown, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

            var html = _held.TryRemove(markdown, out var held)
                ? await held.Task.WaitAsync(cancellationToken)
                : _results[markdown];
            return (TValue)(object)html;
        }
    }
}
