using System.Net;
using System.Reflection;
using System.Text;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ProviderUsagePanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ControlledUsageHandler _handler = new();

    public ProviderUsagePanelTests()
    {
        _ctx.Services.AddSingleton(new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") });
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task Opening_activates_modal_focus_with_the_dialog_and_opener()
    {
        _handler.Enqueue(60, UsageJson(60, requests: 1));
        var cut = _ctx.Render<ProviderUsagePanel>();
        var opener = new ElementReference("usage-opener");

        await cut.InvokeAsync(() => cut.Instance.OpenAsync(opener));

        var invocation = Assert.Single(
            _ctx.JSInterop.Invocations,
            call => call.Identifier == "BotNexus.modalFocus.activate");
        Assert.Equal(2, invocation.Arguments.Count);
        Assert.IsType<ElementReference>(invocation.Arguments[0]);
        Assert.Equal(opener, Assert.IsType<ElementReference>(invocation.Arguments[1]));
        Assert.Equal("dialog", cut.Find("[data-testid='provider-usage-panel']").GetAttribute("role"));
    }

    [Fact]
    public async Task Escape_closes_the_modal_and_restores_focus_to_the_opener()
    {
        _handler.Enqueue(60, UsageJson(60, requests: 1));
        var cut = _ctx.Render<ProviderUsagePanel>();
        var opener = new ElementReference("usage-opener");
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(opener));

        cut.Find("[data-testid='provider-usage-panel']").KeyDown(Key.Escape);

        Assert.Empty(cut.FindAll("[data-testid='provider-usage-panel']"));
        var invocation = Assert.Single(
            _ctx.JSInterop.Invocations,
            call => call.Identifier == "BotNexus.modalFocus.deactivate");
        Assert.True(Assert.IsType<bool>(invocation.Arguments[1]));
    }

    [Fact]
    public async Task Close_button_deactivates_modal_focus_and_restores_the_opener()
    {
        _handler.Enqueue(60, UsageJson(60, requests: 1));
        var cut = _ctx.Render<ProviderUsagePanel>();
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(new ElementReference("usage-opener")));

        cut.Find("[data-testid='usage-close']").Click();

        Assert.Empty(cut.FindAll("[data-testid='provider-usage-panel']"));
        Assert.Contains(
            _ctx.JSInterop.Invocations,
            call => call.Identifier == "BotNexus.modalFocus.deactivate"
                && call.Arguments.Count == 2
                && call.Arguments[1] is true);
    }

    [Fact]
    public async Task Disposal_deactivates_focus_without_restoring_a_stale_opener()
    {
        _handler.Enqueue(60, UsageJson(60, requests: 1));
        var cut = _ctx.Render<ProviderUsagePanel>();
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(new ElementReference("usage-opener")));

        await cut.Instance.DisposeAsync();

        Assert.Contains(
            _ctx.JSInterop.Invocations,
            call => call.Identifier == "BotNexus.modalFocus.deactivate"
                && call.Arguments.Count == 2
                && call.Arguments[1] is false);
    }

    [Fact]
    public async Task Close_during_initial_refresh_cannot_start_polling_after_response_completes()
    {
        var response = _handler.Enqueue(60, UsageJson(60, requests: 1), held: true, ignoreCancellation: true);
        var cut = _ctx.Render<ProviderUsagePanel>();

        var opening = cut.InvokeAsync(() => cut.Instance.OpenAsync());
        await response.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cut.WaitForAssertion(() => cut.Find("[data-testid='usage-close']").Click());

        response.Release();
        await opening.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(PollTimer(cut.Instance));
        Assert.Empty(cut.FindAll("[data-testid='provider-usage-panel']"));
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Dispose_during_initial_refresh_cannot_mutate_or_start_polling_after_response_completes()
    {
        var response = _handler.Enqueue(60, UsageJson(60, requests: 1), held: true, ignoreCancellation: true);
        var cut = _ctx.Render<ProviderUsagePanel>();
        var component = cut.Instance;

        var opening = cut.InvokeAsync(() => component.OpenAsync());
        await response.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await component.DisposeAsync();

        response.Release();
        await opening.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(PollTimer(component));
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Reopening_invalidates_the_old_open_and_keeps_only_the_new_poll_owner()
    {
        var stale = _handler.Enqueue(60, UsageJson(60, requests: 1), held: true, ignoreCancellation: true);
        _handler.Enqueue(60, UsageJson(60, requests: 2));
        var cut = _ctx.Render<ProviderUsagePanel>();

        var firstOpen = cut.InvokeAsync(() => cut.Instance.OpenAsync());
        await stale.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync()).WaitAsync(TimeSpan.FromSeconds(30));
        var currentTimer = PollTimer(cut.Instance);

        stale.Release();
        await firstOpen.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(currentTimer);
        Assert.Same(currentTimer, PollTimer(cut.Instance));
        cut.WaitForAssertion(() => Assert.Contains("2", cut.Find(".usage-totals").TextContent, StringComparison.Ordinal));
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Older_window_success_cannot_replace_newer_window_totals()
    {
        _handler.Enqueue(60, UsageJson(60, requests: 60));
        var oldWindow = _handler.Enqueue(15, UsageJson(15, requests: 15), held: true, ignoreCancellation: true);
        var currentWindow = _handler.Enqueue(360, UsageJson(360, requests: 360), held: true, ignoreCancellation: true);
        var cut = _ctx.Render<ProviderUsagePanel>();
        await cut.InvokeAsync(() => cut.Instance.OpenAsync()).WaitAsync(TimeSpan.FromSeconds(30));

        var firstChange = ChangeWindowAsync(cut, 15);
        await oldWindow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var secondChange = ChangeWindowAsync(cut, 360);
        await currentWindow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        currentWindow.Release();
        await secondChange.WaitAsync(TimeSpan.FromSeconds(30));
        oldWindow.Release();
        await firstChange.WaitAsync(TimeSpan.FromSeconds(30));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("360", cut.Find("[data-testid='usage-window-select']").GetAttribute("value"));
            Assert.Contains("360", cut.Find(".usage-totals").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Older_window_failure_cannot_replace_newer_window_success()
    {
        _handler.Enqueue(60, UsageJson(60, requests: 60));
        var oldWindow = _handler.Enqueue(15, null, held: true, ignoreCancellation: true, status: HttpStatusCode.InternalServerError);
        var currentWindow = _handler.Enqueue(360, UsageJson(360, requests: 360), held: true, ignoreCancellation: true);
        var cut = _ctx.Render<ProviderUsagePanel>();
        await cut.InvokeAsync(() => cut.Instance.OpenAsync()).WaitAsync(TimeSpan.FromSeconds(30));

        var firstChange = ChangeWindowAsync(cut, 15);
        await oldWindow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var secondChange = ChangeWindowAsync(cut, 360);
        await currentWindow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        currentWindow.Release();
        await secondChange.WaitAsync(TimeSpan.FromSeconds(30));
        oldWindow.Release();
        await firstChange.WaitAsync(TimeSpan.FromSeconds(30));

        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("Could not load usage", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("360", cut.Find(".usage-totals").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Unknown_identity_unavailable_split_and_combined_total_are_rendered_without_collision_or_duplication()
    {
        _handler.Enqueue(60, UsageJson(
            60,
            requests: 2,
            modelsJson: """
                [
                  { "model": null, "modelKnown": false, "modelDisplayName": "Unknown model", "requests": 1, "failures": 0, "inputTokens": null, "outputTokens": null, "totalTokens": null },
                  { "model": "unknown", "modelKnown": true, "modelDisplayName": "unknown", "requests": 1, "failures": 0, "inputTokens": null, "outputTokens": null, "totalTokens": 15 }
                ]
                """,
            inputTokens: null,
            outputTokens: null,
            totalTokens: 15));
        var cut = _ctx.Render<ProviderUsagePanel>();

        await cut.InvokeAsync(() => cut.Instance.OpenAsync());

        var totals = cut.Find(".usage-totals").TextContent;
        Assert.Contains("15 total tokens", totals, StringComparison.Ordinal);
        Assert.DoesNotContain(" in", totals, StringComparison.Ordinal);
        Assert.DoesNotContain(" out", totals, StringComparison.Ordinal);
        var names = cut.FindAll(".usage-model-name").Select(element => element.TextContent).ToList();
        Assert.Equal(["Unknown model", "unknown"], names);
        Assert.Contains("unavailable in / unavailable out", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_empty_provider_state_fetches_account_quota_once_and_renders_unknown_usage_honestly()
    {
        _handler.Enqueue(60, """{"windowMinutes":60,"isTruncated":false,"providers":[]}""");
        _handler.Enqueue(15, """{"windowMinutes":15,"isTruncated":false,"providers":[]}""");
        _handler.QuotaObservedAtUtc = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var cut = _ctx.Render<ProviderUsagePanel>();

        await cut.InvokeAsync(() => cut.Instance.OpenAsync());

        Assert.Contains("No provider calls observed yet", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Copilot account quota", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("250 of 500 provider quota units remaining", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Sources and units are not interchangeable", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(1, _handler.AccountQuotaRequests);

        var selectWindow = typeof(ProviderUsagePanel).GetMethod("SelectWindow", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(selectWindow);
        await cut.InvokeAsync(() => (Task)selectWindow.Invoke(cut.Instance, [new ChangeEventArgs { Value = "15" }])!);

        Assert.Equal(1, _handler.AccountQuotaRequests);
    }

    [Fact]
    public async Task Copilot_account_quota_is_rendered_alongside_existing_provider_usage()
    {
        _handler.Enqueue(60, UsageJson(60, requests: 4));
        var cut = _ctx.Render<ProviderUsagePanel>();

        await cut.InvokeAsync(() => cut.Instance.OpenAsync());

        Assert.Contains("Copilot account quota", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("250 of 500 provider quota units remaining", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("4 wire attempts", cut.Find(".usage-totals").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("No provider calls observed yet", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_copilot_quota_is_shown_as_unknown_not_zero()
    {
        _handler.QuotaNoData = true;
        _handler.Enqueue(60, UsageJson(60, requests: 1));
        var cut = _ctx.Render<ProviderUsagePanel>();

        await cut.InvokeAsync(() => cut.Instance.OpenAsync());

        Assert.Contains("No premium-interaction quota data is available", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("0 of 0", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unlimited_copilot_quota_is_shown_as_unlimited()
    {
        _handler.QuotaUnlimited = true;
        _handler.Enqueue(60, UsageJson(60, requests: 1));
        var cut = _ctx.Render<ProviderUsagePanel>();

        await cut.InvokeAsync(() => cut.Instance.OpenAsync());

        Assert.Contains("Premium interactions: unlimited", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("250 of 500", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stale_copilot_quota_is_not_presented_as_current()
    {
        _handler.QuotaObservedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-11).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        _handler.Enqueue(60, UsageJson(60, requests: 4));
        var cut = _ctx.Render<ProviderUsagePanel>();

        await cut.InvokeAsync(() => cut.Instance.OpenAsync());

        Assert.Contains("Premium-interaction quota data is stale", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("250 of 500 provider quota units remaining", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copilot_quota_failure_is_explicit_and_does_not_hide_provider_usage()
    {
        _handler.QuotaFailure = true;
        _handler.Enqueue(60, UsageJson(60, requests: 4));
        var cut = _ctx.Render<ProviderUsagePanel>();

        await cut.InvokeAsync(() => cut.Instance.OpenAsync());

        Assert.Contains("Copilot account quota is currently unavailable", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("4 wire attempts", cut.Find(".usage-totals").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Could not load usage", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Truncated_window_displays_explicit_incomplete_message()
    {
        _handler.Enqueue(60, UsageJson(60, requests: 20_000, isTruncated: true));
        var cut = _ctx.Render<ProviderUsagePanel>();

        await cut.InvokeAsync(() => cut.Instance.OpenAsync());

        var warning = cut.Find("[data-testid='usage-truncated']").TextContent;
        Assert.Contains("incomplete", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("global retention", warning, StringComparison.OrdinalIgnoreCase);
    }

    private static Task ChangeWindowAsync(IRenderedComponent<ProviderUsagePanel> cut, int minutes)
    {
        var method = typeof(ProviderUsagePanel).GetMethod("SelectWindow", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [new ChangeEventArgs { Value = minutes.ToString() }])!);
    }

    private static Timer? PollTimer(ProviderUsagePanel component)
    {
        var field = typeof(ProviderUsagePanel).GetField("_poll", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (Timer?)field.GetValue(component);
    }

    private static string UsageJson(
        int windowMinutes,
        int requests,
        bool isTruncated = false,
        string? modelsJson = null,
        long? inputTokens = 10,
        long? outputTokens = 5,
        long? totalTokens = null)
    {
        modelsJson ??= $$"""
            [{ "model": "example-model", "modelKnown": true, "modelDisplayName": "example-model", "requests": {{requests}}, "failures": 0, "inputTokens": 10, "outputTokens": 5, "totalTokens": null }]
            """;
        return $$"""
            {
              "windowMinutes": {{windowMinutes}},
              "isTruncated": {{isTruncated.ToString().ToLowerInvariant()}},
              "providers": [
                {
                  "provider": "example",
                  "observedAtUtc": "2026-09-15T00:00:00Z",
                  "limits": [],
                  "burn": {
                    "requests": {{requests}},
                    "failures": 0,
                    "inputTokens": {{JsonNumber(inputTokens)}},
                    "outputTokens": {{JsonNumber(outputTokens)}},
                    "totalTokens": {{JsonNumber(totalTokens)}},
                    "models": {{modelsJson}}
                  }
                }
              ]
            }
            """;
    }

    private static string JsonNumber(long? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";

    private sealed class ControlledUsageHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, Queue<Response>> _responses = [];

        public List<int> Requests { get; } = [];
        public int AccountQuotaRequests { get; private set; }
        public bool QuotaFailure { get; set; }
        public bool QuotaNoData { get; set; }
        public bool QuotaUnlimited { get; set; }
        public string QuotaObservedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        public Response Enqueue(
            int windowMinutes,
            string? body,
            bool held = false,
            bool ignoreCancellation = false,
            HttpStatusCode status = HttpStatusCode.OK)
        {
            var response = new Response(body, status, held, ignoreCancellation);
            lock (_gate)
            {
                if (!_responses.TryGetValue(windowMinutes, out var queue))
                {
                    queue = new Queue<Response>();
                    _responses.Add(windowMinutes, queue);
                }
                queue.Enqueue(response);
            }
            return response;
        }

        private string AccountJson()
        {
            var stale = DateTimeOffset.Parse(QuotaObservedAtUtc, System.Globalization.CultureInfo.InvariantCulture) < DateTimeOffset.UtcNow.AddMinutes(-5);
            var snapshots = QuotaNoData ? "[]" : $"[{{\"quotaId\":\"premium_interactions\",\"entitlement\":500,\"remaining\":250,\"percentRemaining\":50,\"isUnlimited\":{QuotaUnlimited.ToString().ToLowerInvariant()},\"resetDate\":\"2099-01-01\",\"observedAtUtc\":\"{QuotaObservedAtUtc}\"}}]";
            return $"{{\"instance\":\"github-copilot\",\"state\":\"{(QuotaNoData ? "unavailable" : stale ? "stale" : "fresh")}\",\"isStale\":{stale.ToString().ToLowerInvariant()},\"snapshots\":{snapshots},\"attemptState\":\"{(QuotaFailure ? "error" : "success")}\",\"lastSuccessAtUtc\":\"{QuotaObservedAtUtc}\"}}";
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/copilot/quota/refresh")
            {
                AccountQuotaRequests++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(AccountJson(), Encoding.UTF8, "application/json")
                };
            }
            if (request.RequestUri?.AbsolutePath == "/api/providers/usage/details")
            {
                if (QuotaFailure) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                var account = AccountJson();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"instance\":\"github-copilot\",\"availableInstances\":[{{\"instance\":\"github-copilot\",\"type\":\"github-copilot\"}}],\"account\":{account},\"headers\":[],\"scheduled\":{{\"state\":\"unavailable\"}}}}", Encoding.UTF8, "application/json")
                };
            }

            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri?.Query ?? string.Empty);
            var windowMinutes = int.Parse(query["windowMinutes"]!, System.Globalization.CultureInfo.InvariantCulture);
            Response response;
            lock (_gate)
            {
                Requests.Add(windowMinutes);
                response = _responses[windowMinutes].Dequeue();
            }

            response.Entered.TrySetResult();
            if (response.IsHeld)
            {
                if (response.IgnoreCancellation)
                    await response.Completion.Task;
                else
                    await response.Completion.Task.WaitAsync(cancellationToken);
            }

            return new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body ?? "error", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class Response(
        string? body,
        HttpStatusCode status,
        bool isHeld,
        bool ignoreCancellation)
    {
        public string? Body { get; } = body;
        public HttpStatusCode Status { get; } = status;
        public bool IsHeld { get; } = isHeld;
        public bool IgnoreCancellation { get; } = ignoreCancellation;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Completion.TrySetResult();
    }
}
