using System.Collections.Concurrent;
using System.Net;
using System.Text;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class GuideTests : IDisposable
{
    private const string IndexJson = """
        {
          "sections": [
            { "id": "alpha", "file": "alpha.md", "title": "Alpha" },
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
        _http.SetBody("/guide/beta.md", "# Beta");
        _http.SetBody("/guide/gamma.md", "# Gamma");
        _js.SetRendered("# Alpha", "<h1>Alpha</h1>");
        _js.SetRendered("# Beta", "<h1>Beta</h1>");
        _js.SetRendered("# Gamma", "<h1>Gamma</h1>");

        _ctx.Services.AddSingleton(new HttpClient(_http) { BaseAddress = new Uri("http://localhost/") });
        _ctx.Services.AddSingleton<IJSRuntime>(_js);
    }

    public void Dispose() => _ctx.Dispose();

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
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _requested = new(StringComparer.Ordinal);

        public void SetBody(string path, string body, string mediaType = "text/plain") =>
            _responses[path] = () => Response(body, mediaType: mediaType);

        public TaskCompletionSource<HttpResponseMessage> HoldNext(string path)
        {
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _held[path] = response;
            return response;
        }

        public Task WaitForRequestAsync(string path) =>
            _requested.GetOrAdd(path, _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task.WaitAsync(TimeSpan.FromSeconds(30));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            _requested.GetOrAdd(path, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

            if (_held.TryRemove(path, out var held))
                return await held.Task.WaitAsync(cancellationToken);

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
