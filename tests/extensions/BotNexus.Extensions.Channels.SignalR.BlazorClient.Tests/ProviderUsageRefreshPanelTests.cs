using System.Net;
using System.Reflection;
using System.Text;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>Account refresh acceptance contracts against an initially empty per-instance cache.</summary>
public sealed class ProviderUsageRefreshPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly RefreshHandler _handler = new();

    public ProviderUsageRefreshPanelTests()
    {
        _ctx.Services.AddSingleton(new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") });
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task Named_selection_refreshes_empty_cache_normally_and_local_reads_never_refresh()
    {
        var cut = await OpenAsync();
        await RefreshTask(cut);
        Assert.Contains("11 of 100", Account(cut));
        await SelectAsync(cut, "work-copilot");
        await RefreshTask(cut);
        Assert.Equal(new[] { "github-copilot:false", "work-copilot:false" }, _handler.Posts);
        Assert.Contains("22 of 100", Account(cut));
        Assert.DoesNotContain("11 of 100", Account(cut));
        for (var i = 0; i < 100; i++) await InvokePrivate(cut, "PollOnceAsync");
        Assert.Equal(2, _handler.Posts.Count);
        Assert.False(cut.Find("[data-testid='account-refresh']").HasAttribute("disabled"));
        await cut.Find("[data-testid='account-refresh']").ClickAsync(new());
        Assert.Equal("work-copilot:true", _handler.Posts.Last());
        Assert.Equal(1, _handler.MaxActivePosts);
    }

    [Fact]
    public async Task Rapid_switching_keeps_one_physical_post_and_only_latest_pending_instance()
    {
        _handler.HoldFirstPost = true;
        var cut = await OpenAsync();
        await _handler.PostEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("11 of 100", Account(cut));
        for (var i = 0; i < 100; i++) await SelectAsync(cut, i % 2 == 0 ? "work-copilot" : "personal-copilot");
        Assert.Equal(new[] { "github-copilot:false" }, _handler.Posts);
        Assert.Equal(1, _handler.ActivePosts);
        Assert.NotNull(PrivateField(cut, "_refreshPending"));
        Assert.True(cut.Find("[data-testid='account-refresh']").HasAttribute("disabled"));
        await InvokePrivate(cut, "ManualRefreshAsync");
        Assert.Equal(new[] { "github-copilot:false" }, _handler.Posts);
        Assert.Contains("loading", cut.Find("[data-testid='account-refresh-state']").TextContent);
        Assert.DoesNotContain("11 of 100", Account(cut));
        _handler.ReleasePost.TrySetResult();
        await RefreshTask(cut);
        cut.WaitForAssertion(() => Assert.Contains("33 of 100", Account(cut)));
        Assert.Equal(new[] { "github-copilot:false", "personal-copilot:false" }, _handler.Posts);
        Assert.Equal(1, _handler.MaxActivePosts);
        Assert.DoesNotContain("11 of 100", Account(cut));
        Assert.DoesNotContain("22 of 100", Account(cut));
        Assert.False(cut.Find("[data-testid='account-refresh']").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_discards_pending_refresh_even_when_old_post_ignores_cancellation(bool dispose)
    {
        _handler.HoldFirstPost = true;
        var cut = await OpenAsync();
        await _handler.PostEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SelectAsync(cut, "work-copilot");
        if (dispose) await cut.InvokeAsync(async () => await cut.Instance.DisposeAsync());
        else await cut.Find("[data-testid='usage-close']").ClickAsync(new());
        var reads = _handler.Reads;
        _handler.ReleasePost.TrySetResult();
        await RefreshTask(cut);
        Assert.Equal(new[] { "github-copilot:false" }, _handler.Posts);
        Assert.Equal(reads, _handler.Reads);
        Assert.Equal(0, _handler.ActivePosts);
        await InvokePrivate(cut, "PollOnceAsync");
        Assert.Equal(reads, _handler.Reads);
        Assert.Equal(new[] { "github-copilot:false" }, _handler.Posts);
        if (dispose)
        {
            // Direct disposal tears down ownership, not bUnit's last rendered DOM snapshot.
            Assert.Equal(true, PrivateField(cut, "_disposed"));
            Assert.Equal(false, PrivateField(cut, "_show"));
            Assert.Null(PrivateField(cut, "_poll"));
        }
        else Assert.Empty(cut.FindAll("[data-testid='provider-usage-panel']"));
    }

    [Fact]
    public async Task Reopen_does_not_overlap_old_post_and_recovers_loading()
    {
        _handler.HoldFirstPost = true;
        var cut = await OpenAsync();
        await _handler.PostEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cut.Find("[data-testid='usage-close']").ClickAsync(new());
        await cut.InvokeAsync(() => cut.Instance.OpenAsync());
        Assert.Single(_handler.Posts);
        Assert.Contains("loading", cut.Find("[data-testid='account-refresh-state']").TextContent);
        _handler.ReleasePost.TrySetResult();
        await RefreshTask(cut);
        cut.WaitForAssertion(() => Assert.Contains("11 of 100", Account(cut)));
        Assert.Equal(1, _handler.MaxActivePosts);
        Assert.False(cut.Find("[data-testid='account-refresh']").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Window_changes_keep_initial_post_owned_without_creating_another_refresh(bool scheduled)
    {
        _handler.HoldFirstPost = true;
        var cut = await OpenAsync();
        await _handler.PostEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var method = typeof(ProviderUsagePanel).GetMethod(scheduled ? "SelectScheduledWindow" : "SelectWindow", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        await cut.InvokeAsync(() => Assert.IsAssignableFrom<Task>(method.Invoke(cut.Instance, [new ChangeEventArgs { Value = scheduled ? "utc-day" : "15" }])));
        Assert.False(_handler.FirstPostToken.IsCancellationRequested);
        Assert.Single(_handler.Posts);
        Assert.Contains("loading", cut.Find("[data-testid='account-refresh-state']").TextContent);
        _handler.ReleasePost.TrySetResult();
        await RefreshTask(cut);
        cut.WaitForAssertion(() => Assert.Contains("11 of 100", Account(cut)));
        Assert.Equal(new[] { "github-copilot:false" }, _handler.Posts);
        Assert.False(cut.Find("[data-testid='account-refresh']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Failed_refresh_releases_loading_and_manual_retry_recovers_without_leaking_diagnostics()
    {
        _handler.FailFirstPost = true;
        var cut = await OpenAsync();
        await RefreshTask(cut);
        Assert.Contains("Copilot account quota is currently unavailable", Account(cut));
        Assert.DoesNotContain("PRIVATE_DIAGNOSTIC", cut.Markup);
        Assert.False(cut.Find("[data-testid='account-refresh']").HasAttribute("disabled"));
        await cut.Find("[data-testid='account-refresh']").ClickAsync(new());
        Assert.Equal(new[] { "github-copilot:false", "github-copilot:true" }, _handler.Posts);
        Assert.Contains("11 of 100", Account(cut));
        Assert.Equal(0, _handler.ActivePosts);
        Assert.Equal(1, _handler.MaxActivePosts);
    }

    [Fact]
    public async Task Http_scope_disclaimer_is_visible_without_any_http_provider_entries()
    {
        var cut = await OpenAsync();
        var scope = cut.Find("[data-testid='http-activity-scope']").TextContent;
        Assert.Contains("HTTP", scope);
        Assert.Contains("WebSocket", scope);
        Assert.Contains("not total Copilot activity", scope);
        Assert.DoesNotContain("No provider calls observed yet", cut.Markup);
        Assert.Contains("No HTTP responses observed", cut.Markup);
    }

    private static object? PrivateField(IRenderedComponent<ProviderUsagePanel> cut, string name)
    {
        var field = typeof(ProviderUsagePanel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return field.GetValue(cut.Instance);
    }
    private static string Account(IRenderedComponent<ProviderUsagePanel> cut) => cut.Find("[data-testid='copilot-account-quota']").TextContent;
    private static Task SelectAsync(IRenderedComponent<ProviderUsagePanel> cut, string instance) => cut.Find("[data-testid='copilot-instance-select']").ChangeAsync(new ChangeEventArgs { Value = instance });
    private async Task<IRenderedComponent<ProviderUsagePanel>> OpenAsync()
    {
        var cut = _ctx.Render<ProviderUsagePanel>();
        await cut.InvokeAsync(() => cut.Instance.OpenAsync()).WaitAsync(TimeSpan.FromSeconds(10));
        return cut;
    }
    private static Task RefreshTask(IRenderedComponent<ProviderUsagePanel> cut)
    {
        var field = typeof(ProviderUsagePanel).GetField("_refreshTask", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<Task>(field.GetValue(cut.Instance)).WaitAsync(TimeSpan.FromSeconds(10));
    }
    private static Task InvokePrivate(IRenderedComponent<ProviderUsagePanel> cut, string name)
    {
        var method = typeof(ProviderUsagePanel).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return cut.InvokeAsync(() => Assert.IsAssignableFrom<Task>(method.Invoke(cut.Instance, null)));
    }

    private sealed class RefreshHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _cache = [];
        public List<string> Posts { get; } = [];
        public bool HoldFirstPost { get; set; }
        public bool FailFirstPost { get; set; }
        public CancellationToken FirstPostToken { get; private set; }
        public int ActivePosts { get; private set; }
        public int MaxActivePosts { get; private set; }
        public int Reads { get; private set; }
        public TaskCompletionSource PostEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing URI");
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var instance = query["instance"] ?? "github-copilot";
            if (request.Method == HttpMethod.Post)
            {
                Posts.Add($"{instance}:{query["force"]}");
                ActivePosts++;
                MaxActivePosts = Math.Max(MaxActivePosts, ActivePosts);
                try
                {
                    if (Posts.Count == 1) FirstPostToken = cancellationToken;
                    PostEntered.TrySetResult();
                    if (FailFirstPost && Posts.Count == 1)
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("PRIVATE_DIAGNOSTIC") };
                    if (HoldFirstPost && Posts.Count == 1) await ReleasePost.Task; // Physical transport deliberately ignores cancellation.
                    _cache[instance] = instance == "work-copilot" ? 22 : instance == "personal-copilot" ? 33 : 11;
                    return Json("{}");
                }
                finally { ActivePosts--; }
            }
            Reads++;
            if (uri.AbsolutePath.EndsWith("/details", StringComparison.Ordinal))
            {
                var account = _cache.TryGetValue(instance, out var remaining)
                    ? $$"""{"state":"fresh","attemptState":"success","snapshots":[{"quotaId":"premium_interactions","entitlement":100,"remaining":{{remaining}},"isUnlimited":false}]}"""
                    : "{\"state\":\"unavailable\",\"snapshots\":[]}";
                return Json($$"""{"instance":"{{instance}}","availableInstances":[{"instance":"github-copilot","type":"github-copilot"},{"instance":"work-copilot","type":"github-copilot"},{"instance":"personal-copilot","type":"copilot"}],"account":{{account}}}""");
            }
            return Json("{\"windowMinutes\":60,\"providers\":[]}");
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
