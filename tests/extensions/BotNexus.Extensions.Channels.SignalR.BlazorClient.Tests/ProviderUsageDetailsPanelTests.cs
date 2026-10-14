using System.Net;
using System.Reflection;
using System.Text;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>Companion-source rendering and explicit-refresh lifecycle contracts.</summary>
public sealed class ProviderUsageDetailsPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly DetailsHandler _handler = new();
    private readonly PanelClock _clock = new();

    /// <summary>Installs isolated HTTP and modal-focus boundaries.</summary>
    public ProviderUsageDetailsPanelTests()
    {
        _ctx.Services.AddSingleton(new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") });
        _ctx.Services.AddSingleton<TimeProvider>(_clock);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>Releases rendered components and their polling ownership.</summary>
    public void Dispose() => _ctx.Dispose();

    [Fact]
    /// <summary>Verifies default is closed and does not request data.</summary>
    public void Default_is_closed_and_does_not_request_data()
    {
        var cut = _ctx.Render<ProviderUsagePanel>();
        Assert.Empty(cut.FindAll("[data-testid='provider-usage-panel']"));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    /// <summary>Verifies fractional account and disagreeing header fields remain independent.</summary>
    public async Task Fractional_account_and_disagreeing_header_fields_remain_independent()
    {
        var cut = await OpenAsync();
        var account = cut.Find("[data-testid='copilot-account-quota']").TextContent;
        Assert.Contains("0.125 of 1.5 provider quota units remaining", account);
        Assert.Contains("1.375 provider quota units used", account);
        var headers = cut.Find("[data-testid='copilot-header-quota']").TextContent;
        Assert.Contains("12.5% remaining", headers);
        Assert.Contains("0.75 provider quota units remaining", headers);
        Assert.Contains("independent observations", cut.Markup);
        Assert.DoesNotContain("tokens used", account);
        Assert.DoesNotContain("$", account);
    }

    [Theory]
    [InlineData("null", "null", "false", "unknown of unknown")]
    [InlineData("1", "2", "false", "2 of 1")]
    [InlineData("0", "0", "true", "unlimited")]
    /// <summary>Verifies unknown incompatible and unlimited quota never derives used.</summary>
    public async Task Unknown_incompatible_and_unlimited_quota_never_derives_used(string entitlement, string remaining, string unlimited, string expected)
    {
        _handler.Account = Account(entitlement, remaining, unlimited);
        var cut = await OpenAsync();
        var text = cut.Find("[data-testid='copilot-account-quota']").TextContent;
        Assert.Contains(expected, text);
        Assert.DoesNotContain("provider quota units used", text);
    }

    [Fact]
    /// <summary>Verifies zero remaining is not unknown.</summary>
    public async Task Zero_remaining_is_not_unknown()
    {
        _handler.Account = Account("1.5", "0", "false");
        var cut = await OpenAsync();
        Assert.Contains("0 of 1.5 provider quota units remaining", cut.Markup);
        Assert.Contains("1.5 provider quota units used", cut.Markup);
    }

    [Fact]
    /// <summary>Verifies scheduled totals and coverage are gateway wide not top job sums.</summary>
    public async Task Scheduled_totals_and_coverage_are_gateway_wide_not_top_job_sums()
    {
        var cut = await OpenAsync();
        var scheduled = cut.Find("[data-testid='scheduled-usage']");
        Assert.Null(scheduled.ParentElement?.Closest("[data-testid='copilot-account-quota']"));
        var text = scheduled.TextContent;
        Assert.Contains("100 runs", text);
        Assert.Contains("900 total tokens", text);
        Assert.Contains("80 measured / 20 unmeasured", text);
        Assert.Contains("2 running / 3 unfinalized", text);
        Assert.Contains("70/100", text);
        Assert.Contains("60/100", text);
        Assert.Contains("retention", text);
        Assert.Contains("Cache tokens: unsupported", text);
        Assert.Contains("All-conversation burn: unsupported", text);
        Assert.Contains("delegated overlap", text);
        Assert.Contains("harmless-job-id", text);
        Assert.Contains("2026-10-01", text);
    }

    [Fact]
    /// <summary>Verifies empty available scheduled interval is not unavailable.</summary>
    public async Task Empty_available_scheduled_interval_is_not_unavailable()
    {
        _handler.Scheduled = Scheduled.Replace("\"runCount\":100", "\"runCount\":0", StringComparison.Ordinal)
            .Replace("\"topJobs\":[{\"jobId\":\"harmless-job-id\",\"totals\":{\"runCount\":1,\"totalTokens\":5}}]", "\"topJobs\":[]", StringComparison.Ordinal);
        var cut = await OpenAsync();
        Assert.Contains("0 runs", cut.Find("[data-testid='scheduled-totals']").TextContent);
        Assert.Contains("No scheduled jobs in this interval", cut.Markup);
        Assert.DoesNotContain("Scheduled activity is currently unavailable", cut.Markup);
        Assert.Equal("24h", cut.Find("[data-testid='scheduled-window-select']").GetAttribute("value"));
    }

    [Fact]
    /// <summary>Verifies stale error retains last success with attempt and partial labels.</summary>
    public async Task Stale_error_retains_last_success_with_attempt_and_partial_labels()
    {
        _handler.Account = Account().Replace("\"fresh\"", "\"stale\"", StringComparison.Ordinal)
            .Replace("\"success\"", "\"error\"", StringComparison.Ordinal);
        var cut = await OpenAsync();
        Assert.Contains("0.125 of 1.5", cut.Markup);
        Assert.Contains("stale", cut.Find("[data-testid='account-refresh-state']").TextContent);
        Assert.Contains("error", cut.Find("[data-testid='account-refresh-state']").TextContent);
        Assert.Contains("partial", cut.Find("[data-testid='account-refresh-state']").TextContent);
        Assert.Contains("Last success 2026-10-01 12:00:00 UTC", cut.Markup);
        Assert.Contains("Last attempt 2026-10-01 12:01:00 UTC", cut.Markup);
    }

    [Fact]
    /// <summary>Verifies unknown default account does not infer credentials from configured alias.</summary>
    public async Task Unknown_default_account_does_not_infer_credentials_from_configured_alias()
    {
        _handler.UnknownAccount = true;
        var cut = await OpenAsync();
        Assert.Contains("verified account instance unknown / unavailable", cut.Markup);
        Assert.Contains("Account identity and quota are unknown or unavailable", cut.Markup);
        Assert.Contains("github-copilot", cut.Find("[data-testid='copilot-instance-select']").TextContent);
        Assert.DoesNotContain("0.125 of 1.5", cut.Markup);
    }

    [Fact]
    /// <summary>Verifies held refresh is background loading and close cancels it.</summary>
    public async Task Held_refresh_is_background_loading_and_close_cancels_it()
    {
        _handler.HoldRefresh = true;
        var cut = await OpenAsync();
        await _handler.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("loading", cut.Find("[data-testid='account-refresh-state']").TextContent);
        cut.Find("[data-testid='usage-close']").Click();
        await _handler.RefreshCancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(cut.FindAll("[data-testid='provider-usage-panel']"));
    }

    [Fact]
    /// <summary>Verifies manual refresh forces once and reports server throttle state.</summary>
    public async Task Manual_refresh_forces_once_and_reports_server_throttle_state()
    {
        var cut = await OpenAsync();
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid='account-refresh']").HasAttribute("disabled")));
        _handler.Account = Account().Replace("\"success\"", "\"error\"", StringComparison.Ordinal);
        await cut.Find("[data-testid='account-refresh']").ClickAsync(new());
        Assert.Equal(2, _handler.Requests.Count(x => x.StartsWith("POST", StringComparison.Ordinal)));
        Assert.Contains(_handler.Requests, x => x.Contains("force=true", StringComparison.Ordinal));
        Assert.Contains("error", cut.Find("[data-testid='account-refresh-state']").TextContent);
        Assert.Contains("Next refresh", cut.Markup);
        Assert.Contains("0.125 of 1.5", cut.Markup);
    }

    [Fact]
    /// <summary>Verifies ordinary poll is serial coalesced and never posts.</summary>
    public async Task Ordinary_poll_is_serial_coalesced_and_never_posts()
    {
        var cut = await OpenAsync();
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid='account-refresh']").HasAttribute("disabled")));
        var posts = _handler.Requests.Count(x => x.StartsWith("POST", StringComparison.Ordinal));
        _handler.HoldDetails = true;
        var first = InvokePrivate(cut, "PollOnceAsync");
        await _handler.DetailsEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 100; i++) await InvokePrivate(cut, "PollOnceAsync");
        Assert.Equal(1, _handler.ActiveDetails);
        Assert.Equal(1, _handler.MaxActiveDetails);
        _handler.ReleaseDetails.TrySetResult();
        await first;
        Assert.Equal(posts, _handler.Requests.Count(x => x.StartsWith("POST", StringComparison.Ordinal)));
    }

    [Fact]
    /// <summary>Verifies instance change suppresses late old account and headers.</summary>
    public async Task Instance_change_suppresses_late_old_account_and_headers()
    {
        var cut = await OpenAsync();
        _handler.HoldDetails = true;
        var oldPoll = InvokePrivate(cut, "PollOnceAsync");
        await _handler.DetailsEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        _handler.HoldDetails = false;
        _handler.Account = Account("500", "99", "false");
        await cut.Find("[data-testid='copilot-instance-select']").ChangeAsync(new ChangeEventArgs { Value = "work-copilot" });
        Assert.DoesNotContain("0.125 of 1.5", cut.Markup);
        Assert.DoesNotContain("12.5% remaining", cut.Markup);
        Assert.Equal(1, _handler.ActiveDetails);
        Assert.DoesNotContain(_handler.Requests, x => x.Contains("instance=work-copilot", StringComparison.Ordinal));
        _handler.ReleaseDetails.TrySetResult();
        await oldPoll.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("0.125", _handler.HeldBodyReturned);
        Assert.Contains("\"instance\":\"github-copilot\"", _handler.HeldBodyReturned);
        Assert.Equal(1, _handler.MaxActiveDetails);
        cut.WaitForAssertion(() => Assert.Contains("99 of 500", cut.Find("[data-testid='copilot-account-quota']").TextContent));
        Assert.DoesNotContain("0.125 of 1.5", cut.Markup);
        Assert.Contains(_handler.Requests, x => x.Contains("instance=work-copilot", StringComparison.Ordinal));
    }

    [Fact]
    /// <summary>Verifies scheduled window change suppresses late old interval.</summary>
    public async Task Scheduled_window_change_suppresses_late_old_interval()
    {
        var cut = await OpenAsync();
        _handler.HoldDetails = true;
        var oldPoll = InvokePrivate(cut, "PollOnceAsync");
        await _handler.DetailsEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        _handler.HoldDetails = false;
        _handler.Scheduled = Scheduled.Replace("900", "1234", StringComparison.Ordinal);
        await cut.Find("[data-testid='scheduled-window-select']").ChangeAsync(new ChangeEventArgs { Value = "utc-day" });
        _handler.ReleaseDetails.TrySetResult();
        await oldPoll;
        Assert.Contains("1.2k total tokens", cut.Find("[data-testid='scheduled-usage']").TextContent);
        Assert.Contains(_handler.Requests, x => x.Contains("startUtc=", StringComparison.Ordinal) && x.Contains("endUtc=", StringComparison.Ordinal));
        Assert.Equal("utc-day", cut.Find("[data-testid='scheduled-window-select']").GetAttribute("value"));
    }

    [Fact]
    /// <summary>Verifies failure displays only generic text and empty sources are explicit.</summary>
    public async Task Failure_displays_only_generic_text_and_empty_sources_are_explicit()
    {
        _handler.FailDetails = true;
        var cut = await OpenAsync();
        Assert.Contains("Copilot account quota is currently unavailable", cut.Markup);
        Assert.DoesNotContain("PRIVATE_DIAGNOSTIC", cut.Markup);
        Assert.Contains("Scheduled activity is currently unavailable", cut.Markup);
    }

    [Fact]
    /// <summary>Verifies disposal cancels refresh and prevents future polling.</summary>
    public async Task Disposal_cancels_refresh_and_prevents_future_polling()
    {
        _handler.HoldRefresh = true;
        var cut = await OpenAsync();
        await _handler.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cut.InvokeAsync(async () => await cut.Instance.DisposeAsync());
        await _handler.RefreshCancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var before = _handler.Requests.Count;
        await InvokePrivate(cut, "PollOnceAsync");
        Assert.Equal(before, _handler.Requests.Count);
    }

    [Theory]
    [InlineData("24h")]
    [InlineData("utc-day")]
    /// <summary>Each owned read recalculates rolling or calendar intervals using the current UTC clock.</summary>
    public async Task Scheduled_interval_advances_on_owned_reads(string window)
    {
        var cut = await OpenAsync();
        if (window == "utc-day")
            await cut.Find("[data-testid='scheduled-window-select']").ChangeAsync(new ChangeEventArgs { Value = window });
        var before = LastInterval();
        _clock.Now = _clock.Now.AddHours(2);
        await InvokePrivate(cut, "PollOnceAsync");
        var after = LastInterval();
        Assert.Equal(window == "24h" ? _clock.Now : new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero), after.End);
        Assert.Equal(after.End.AddDays(-1), after.Start);
        Assert.True(after.End > before.End);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    /// <summary>Initial and manual POST completion coalesce behind a cancellation-ignoring GET.</summary>
    public async Task Post_completion_never_overlaps_held_details(bool manual)
    {
        _handler.HoldRefresh = !manual;
        _handler.HoldDetails = !manual;
        var cut = _ctx.Render<ProviderUsagePanel>();
        var opening = cut.InvokeAsync(() => cut.Instance.OpenAsync());
        Task held = opening;
        if (manual)
        {
            await opening.WaitAsync(TimeSpan.FromSeconds(30));
            cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid='account-refresh']").HasAttribute("disabled")));
            _handler.HoldDetails = true;
            held = InvokePrivate(cut, "PollOnceAsync");
        }
        cut.Find("[data-testid='scheduled-window-select']");
        await _handler.DetailsEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var reads = _handler.Requests.Count(x => x.Contains("/usage/details", StringComparison.Ordinal));
        if (manual) await cut.Find("[data-testid='account-refresh']").ClickAsync(new());
        else
        {
            _handler.ReleaseRefresh.TrySetResult();
            var field = typeof(ProviderUsagePanel).GetField("_refreshTask", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var refresh = Assert.IsAssignableFrom<Task>(field.GetValue(cut.Instance));
            await refresh.WaitAsync(TimeSpan.FromSeconds(30));
        }
        for (var i = 0; i < 100; i++) await InvokePrivate(cut, "PollOnceAsync");
        Assert.Equal(reads, _handler.Requests.Count(x => x.Contains("/usage/details", StringComparison.Ordinal)));
        Assert.Equal(1, _handler.ActiveDetails);
        _handler.HoldDetails = false;
        _handler.ReleaseDetails.TrySetResult();
        await held.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(reads + 1, _handler.Requests.Count(x => x.Contains("/usage/details", StringComparison.Ordinal)));
        Assert.Equal(1, _handler.MaxActiveDetails);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    /// <summary>Closing or disposing removes pending reads even when transport ignores cancellation.</summary>
    public async Task Shutdown_discards_pending_read_and_late_response(bool dispose)
    {
        var cut = await OpenAsync();
        _handler.HoldDetails = true;
        var held = InvokePrivate(cut, "PollOnceAsync");
        await _handler.DetailsEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await InvokePrivate(cut, "PollOnceAsync");
        if (dispose) await cut.InvokeAsync(async () => await cut.Instance.DisposeAsync());
        else await cut.Find("[data-testid='usage-close']").ClickAsync(new());
        var before = _handler.Requests.Count;
        _handler.ReleaseDetails.TrySetResult();
        await held.WaitAsync(TimeSpan.FromSeconds(30));
        await InvokePrivate(cut, "PollOnceAsync");
        Assert.Equal(before, _handler.Requests.Count);
        Assert.Equal(0, _handler.ActiveDetails);
        if (!dispose) Assert.Empty(cut.FindAll("[data-testid='provider-usage-panel']"));
    }

    [Fact]
    /// <summary>Reopening returns without awaiting an old cancellation-ignoring details transport.</summary>
    public async Task Reopen_coalesces_latest_generation_without_waiting_for_old_transport()
    {
        _handler.HoldDetails = true;
        var cut = _ctx.Render<ProviderUsagePanel>();
        var oldOpen = cut.InvokeAsync(() => cut.Instance.OpenAsync());
        cut.Find("[data-testid='scheduled-window-select']");
        await _handler.DetailsEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        _handler.Account = Account("500", "99", "false");
        await cut.InvokeAsync(() => cut.Instance.OpenAsync()).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, _handler.ActiveDetails);
        Assert.DoesNotContain("0.125 of 1.5", cut.Markup);
        _handler.HoldDetails = false;
        _handler.ReleaseDetails.TrySetResult();
        await oldOpen.WaitAsync(TimeSpan.FromSeconds(30));
        cut.WaitForAssertion(() => Assert.Contains("99 of 500", cut.Markup));
        Assert.Equal(1, _handler.MaxActiveDetails);
    }

    [Fact]
    /// <summary>Untrusted selection input is neither reflected nor treated as a configured account.</summary>
    public async Task Unknown_instance_input_is_not_reflected_or_requested()
    {
        var cut = await OpenAsync();
        var before = _handler.Requests.Count;
        await cut.Find("[data-testid='copilot-instance-select']").ChangeAsync(new ChangeEventArgs { Value = "PRIVATE_UNKNOWN_SCOPE" });
        Assert.Equal(before, _handler.Requests.Count);
        Assert.DoesNotContain("PRIVATE_UNKNOWN_SCOPE", cut.Markup);
        Assert.Equal("github-copilot", cut.Find("[data-testid='copilot-instance-select']").GetAttribute("value"));
    }

    private (DateTimeOffset Start, DateTimeOffset End) LastInterval()
    {
        var request = _handler.Requests.Last(x => x.Contains("/usage/details", StringComparison.Ordinal));
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(request[4..]).Query);
        return (DateTimeOffset.Parse(query["startUtc"] ?? throw new InvalidOperationException("Missing start"), System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(query["endUtc"] ?? throw new InvalidOperationException("Missing end"), System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class PanelClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 23, 30, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<IRenderedComponent<ProviderUsagePanel>> OpenAsync()
    {
        var cut = _ctx.Render<ProviderUsagePanel>();
        await cut.InvokeAsync(() => cut.Instance.OpenAsync()).WaitAsync(TimeSpan.FromSeconds(30));
        cut.Find("[data-testid='scheduled-window-select']");
        return cut;
    }

    private static Task InvokePrivate(IRenderedComponent<ProviderUsagePanel> cut, string name)
    {
        var method = typeof(ProviderUsagePanel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return cut.InvokeAsync(() => method.Invoke(cut.Instance, null) is Task task ? task : throw new InvalidOperationException("Expected async seam."));
    }

    private static string Account(string entitlement = "1.5", string remaining = "0.125", string unlimited = "false") => $$"""
        {"instance":"github-copilot","state":"fresh","attemptState":"success","isPartial":true,"lastSuccessAtUtc":"2026-10-01T12:00:00Z","lastAttemptAtUtc":"2026-10-01T12:01:00Z","nextRefreshAtUtc":"2099-01-01T00:00:00Z","snapshots":[{"quotaId":"premium_interactions","entitlement":{{entitlement}},"remaining":{{remaining}},"isUnlimited":{{unlimited}},"isPartial":true,"percentRemaining":8.333,"resetDate":"2026-11-01","observedAtUtc":"2026-10-01T12:00:00Z"}]}
        """;

    private const string Scheduled = """
        {"state":"available","isPartial":true,"data":{"requestedStartInclusiveUtc":"2026-10-01T00:00:00Z","requestedEndExclusiveUtc":"2026-10-02T00:00:00Z","effectiveStartInclusiveUtc":"2026-10-01T01:00:00Z","effectiveEndExclusiveUtc":"2026-10-02T00:00:00Z","windowTruncatedByRetention":true,"windowTruncatedByNow":false,"totals":{"runCount":100,"jobCount":15,"measuredRunCount":80,"unmeasuredRunCount":20,"runningRunCount":2,"unfinalizedRunCount":3,"totalTokens":900,"totalPromptTokens":700,"totalCompletionTokens":200,"totalTurns":40,"totalToolCalls":50,"totalDurationMs":600,"promptTokenRunCount":70,"completionTokenRunCount":60,"turnRunCount":50,"toolCallRunCount":40,"durationRunCount":30},"topJobs":[{"jobId":"harmless-job-id","totals":{"runCount":1,"totalTokens":5}}]}}
        """;

    private sealed class DetailsHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string Account { get; set; } = ProviderUsageDetailsPanelTests.Account();
        public string Scheduled { get; set; } = ProviderUsageDetailsPanelTests.Scheduled;
        public bool HoldRefresh { get; set; }
        public bool HoldDetails { get; set; }
        public bool FailDetails { get; set; }
        public bool UnknownAccount { get; set; }
        public int ActiveDetails { get; private set; }
        public int MaxActiveDetails { get; private set; }
        public string HeldBodyReturned { get; private set; } = "";
        public TaskCompletionSource ReleaseRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RefreshEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RefreshCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DetailsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDetails { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri}");
            if (request.Method == HttpMethod.Post)
            {
                RefreshEntered.TrySetResult();
                if (HoldRefresh)
                {
                    try { await ReleaseRefresh.Task.WaitAsync(cancellationToken); }
                    catch (OperationCanceledException) { RefreshCancelled.TrySetResult(); throw; }
                }
                return Json(Account);
            }
            if (request.RequestUri?.AbsolutePath == "/api/providers/usage/details")
            {
                if (FailDetails) return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("PRIVATE_DIAGNOSTIC") };
                var instance = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["instance"] ?? "github-copilot";
                var body = $$"""
                    {"instance":"{{instance}}","availableInstances":[{"instance":"github-copilot","type":"github-copilot"},{"instance":"work-copilot","type":"github-copilot"}],"account":{{Account}},"headers":[{"quotaId":"premium_interactions","state":"fresh","observedAtUtc":"2026-10-01T12:01:00Z","remainingPercent":12.5,"totalRemainingCount":0.75,"isPartial":true}],"scheduled":{{Scheduled}}}
                    """;
                if (UnknownAccount)
                    body = body.Replace(Account, "{\"state\":\"unavailable\",\"snapshots\":[]}", StringComparison.Ordinal)
                        .Replace($"\"instance\":\"{instance}\"", "\"instance\":null", StringComparison.Ordinal);
                ActiveDetails++;
                MaxActiveDetails = Math.Max(MaxActiveDetails, ActiveDetails);
                try
                {
                    if (HoldDetails)
                    {
                        DetailsEntered.TrySetResult();
                        await ReleaseDetails.Task; // Deliberately returns the captured old account after cancellation.
                        HeldBodyReturned = body;
                    }
                    return Json(body);
                }
                finally { ActiveDetails--; }
            }
            return Json("""{"windowMinutes":60,"isTruncated":false,"providers":[]}""");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
