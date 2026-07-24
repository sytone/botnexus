using System.Net;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests the single-page cron management experience: a rich job selector drives
/// an inline editor and execution history without opening detail/edit modals.
/// </summary>
public sealed class CronJobsPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly CronJobsMockHandler _handler = new();

    public CronJobsPageTests()
    {
        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(httpClient);
        _ctx.Services.AddScoped<CronApiClient>();
        _ctx.Services.AddScoped<SectionsApiClient>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Shows_empty_state_when_no_jobs()
    {
        _handler.SetupResponse("GET", "/api/cron", "[]");

        var cut = _ctx.Render<CronJobs>();
        cut.WaitForState(() => cut.Markup.Contains("No cron jobs configured"));

        Assert.Contains("No cron jobs configured", cut.Markup);
    }

    [Fact]
    public void Rich_selector_lists_name_schedule_type_and_owner()
    {
        SetupJob();

        var cut = _ctx.Render<CronJobs>();
        cut.WaitForState(() => cut.Markup.Contains("Select a cron job"));
        cut.Find("[data-testid='cron-job-selector-toggle']").Click();

        var option = cut.Find("[data-testid='cron-job-option']");
        Assert.Contains("Nightly Report", option.TextContent);
        Assert.Contains("0 2 * * *", option.TextContent);
        Assert.Contains("agent-prompt", option.TextContent);
        Assert.Contains("farnsworth", option.TextContent);
    }

    [Fact]
    public void Selecting_job_shows_inline_editor_and_execution_history()
    {
        SetupJob();
        _handler.SetupResponse("GET", "/api/cron/job-1/runs", JsonSerializer.Serialize(new[]
        {
            new { id = "run-1", jobId = "job-1", startedAt = "2026-07-16T02:00:00Z", completedAt = "2026-07-16T02:00:05Z", status = "ok", sessionId = "cron:job-1" }
        }));
        _handler.SetupResponse("GET", "/api/providers", "[]");

        var cut = _ctx.Render<CronJobs>();
        cut.WaitForState(() => cut.Markup.Contains("Select a cron job"));
        cut.Find("[data-testid='cron-job-selector-toggle']").Click();
        cut.Find("[data-testid='cron-job-option']").Click();

        cut.WaitForState(() => cut.Markup.Contains("cron-job-editor"));
        Assert.Contains("cron-job-editor", cut.Markup);
        Assert.Contains("Edit Cron Job", cut.Markup);
        Assert.DoesNotContain("cron-edit-dialog", cut.Markup);
        Assert.DoesNotContain("cron-detail-modal", cut.Markup);
        cut.WaitForState(() => cut.Markup.Contains("cron-runs-table"));
        Assert.Contains("Execution History", cut.Markup);
        Assert.Contains("cron-runs-table", cut.Markup);
    }

    [Fact]
    public void Inline_editor_shows_provider_and_model_selectors_for_agent_prompt()
    {
        SetupJob();
        _handler.SetupResponse("GET", "/api/cron/job-1/runs", "[]");
        _handler.SetupResponse("GET", "/api/providers", "[]");

        var cut = _ctx.Render<CronJobs>();
        SelectOnlyJob(cut);
        cut.WaitForState(() => cut.Markup.Contains("cron-job-editor"));

        Assert.Contains("cron-edit-provider", cut.Markup);
        Assert.Contains("cron-edit-model", cut.Markup);
    }

    [Fact]
    public void Selected_job_exposes_run_save_and_delete_actions()
    {
        SetupJob();
        _handler.SetupResponse("GET", "/api/cron/job-1/runs", "[]");
        _handler.SetupResponse("GET", "/api/providers", "[]");

        var cut = _ctx.Render<CronJobs>();
        SelectOnlyJob(cut);
        cut.WaitForState(() => cut.Markup.Contains("cron-job-editor"));

        Assert.NotNull(cut.Find("[aria-label='Run Nightly Report now']"));
        Assert.NotNull(cut.Find("[aria-label='Save Nightly Report']"));
        Assert.NotNull(cut.Find("[aria-label='Delete Nightly Report']"));
    }

    [Fact]
    public void Opens_delete_confirmation_from_inline_editor()
    {
        SetupJob();
        _handler.SetupResponse("GET", "/api/cron/job-1/runs", "[]");
        _handler.SetupResponse("GET", "/api/providers", "[]");

        var cut = _ctx.Render<CronJobs>();
        SelectOnlyJob(cut);
        cut.WaitForState(() => cut.Markup.Contains("cron-job-editor"));
        cut.Find("[aria-label='Delete Nightly Report']").Click();

        Assert.Contains("Delete Cron Job", cut.Markup);
        Assert.Contains("This cannot be undone", cut.Markup);
    }

    private void SetupJob()
    {
        _handler.SetupResponse("GET", "/api/cron", JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "job-1",
                name = "Nightly Report",
                schedule = "0 2 * * *",
                actionType = "agent-prompt",
                agentId = "farnsworth",
                message = "Create the report",
                enabled = true,
                conversationId = "conversation-1"
            }
        }));
    }

    private static void SelectOnlyJob(IRenderedComponent<CronJobs> cut)
    {
        cut.WaitForState(() => cut.Markup.Contains("Select a cron job"));
        cut.Find("[data-testid='cron-job-selector-toggle']").Click();
        cut.Find("[data-testid='cron-job-option']").Click();
    }

    private sealed class CronJobsMockHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpResponseMessage> _responses = new(StringComparer.OrdinalIgnoreCase);

        public void SetupResponse(string method, string pathSuffix, string jsonContent)
        {
            _responses[$"{method}:{pathSuffix}"] = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json")
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            var methodKey = $"{request.Method.Method}:{path}";

            foreach (var (key, response) in _responses.OrderByDescending(kv => kv.Key.Length))
            {
                if (methodKey.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
