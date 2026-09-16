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

    public GuideTests()
    {
        _http.SetBody("/guide/guide-index.json", IndexJson, "application/json");
        _http.SetBody("/guide/alpha.md", "# Alpha");
        _http.SetBody("/guide/beta.md", "beta body");
        _http.SetBody("/guide/gamma.md", "gamma body");
        _ctx.Services.AddSingleton(new HttpClient(_http) { BaseAddress = new Uri("http://localhost/") });
        _ctx.Services.AddSingleton<IJSRuntime>(new MarkdownRuntime());
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

    private IRenderedComponent<Guide> RenderGuide()
    {
        var cut = _ctx.Render<Guide>();
        cut.Find("[data-testid='guide-content']").TextContent.ShouldContain("Alpha");
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

    private sealed class MarkdownRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            identifier.ShouldBe("BotNexus.renderMarkdown");
            return ValueTask.FromResult((TValue)(object)$"<p>{args?.ShouldHaveSingleItem()}</p>");
        }
    }
}
